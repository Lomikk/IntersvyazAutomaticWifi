# C# Wi-Fi authorization timing audit

This note records a source-level audit of the current C# authorization path. It focuses on when the background agent wakes, when `stepOne` is allowed to fire, and which transport behaviours can add latency. Protocol facts come from `docs/protocol.md` and the PowerShell/field experiments.

> Update after field validation: cached-IP-first/direct-connect is no longer part of the production C# path. The historical cache fixes below describe an earlier implementation that was subsequently disconnected after real captive measurements showed a small direct-IP gain versus a much larger stale-IP penalty. Production now uses ordinary system DNS. The primary Internet probe is local `http://online.susu.ru/`, not Microsoft Connect Test.

## Current trigger model

The long-running per-user agent does **not** subscribe to a WLAN/network-change event. Its wake-up policy is driven by the persisted `ExpectedExpiryUtc` produced by the last successful `stepTwo`:

- far before the predicted 24-hour edge: sleep up to `AgentPollSeconds` (15 s by default); no Internet/captive probe is performed;
- from `expiry - 10 s` through `expiry + 10 s`: wake every 250 ms by default;
- before the exact expiry, two nearby local SUSU connectivity-probe responses are required before an early `stepOne` is allowed;
- at/after the predicted expiry, the timer itself is authoritative and the agent does not wait for an Internet probe before entering the authorization flow;
- once overdue and outside the guard, a machine that is not currently on `Campus Wi-Fi*` wakes roughly once per second, so joining the target SSID after expiry is noticed quickly.

After the trigger, the critical path is structurally faithful to the validated experiments:

```text
SSID gate
→ baseline /pushmessages
→ POST /stepOne
→ independent absolute-offset /pushmessages polls
→ fresh code
→ direct POST /stepTwo
→ Internet confirmation
```

The baseline is intentionally completed before `stepOne`; otherwise a newly generated Wi-Fi code could be absorbed into the baseline and missed. Polls are launched at the validated absolute offsets and do not wait for older poll responses. A fresh code can therefore advance directly to `stepTwo` even while the browser-facing `stepOne` response is still pending.

## Historical timing defects found in the pre-field-validation implementation

### 1. Background DNS warming could block the only agent loop

Before the guard, `AgentService` awaited `WarmKnownHostsAsync`. The previous implementation resolved tracked hosts sequentially with no warm-only timeout. A slow/wedged system DNS lookup could therefore keep one `TickAsync` alive long enough to miss the expected expiry edge.

That implementation was initially hardened with parallel bounded warmups. Field testing later rejected cached-IP-first for production entirely, so the current agent performs no DNS warmup before the guard and the production request path uses ordinary system DNS.

### 2. A stale multi-address cache multiplied the cached-connect penalty

The DNS connector previously applied the 700 ms cached-connect timeout **per cached address**. With several stale A/AAAA records this could spend most or all of the 3 s baseline timeout before ordinary DNS was attempted.

The 700 ms value was changed to a budget for the whole cached-address phase. Field testing then showed that even one black-holed cached address can cost roughly the whole ~700 ms before DNS, while ordinary DNS cost only tens of milliseconds. Production therefore no longer enters a cached-address phase at all.

### 3. DNS-cache persistence could break a live request

On a DNS refresh the connector persisted the resolved addresses on the live connection path. A local cache-file write problem could fail the HTTP request even though DNS itself had succeeded.

The connector was hardened to persist only after a successful fresh connection and to treat persistence as best-effort. The connector is now retained only for experiments/diagnostics and is not wired into production `HttpClient` instances.

### 4. A portal DNS failure could waste a `stepOne` attempt and wait the full mailbox schedule

The state machine reserves an automatic `stepOne` attempt before sending, which is the correct crash-safe ordering. However, if connecting to `w.is74.ru` then failed with typed `DnsUnavailable`, the transport explicitly knew that no portal side effect could have happened, but the flow still waited through the full polling schedule and kept the attempt charged.

That could add about 10 seconds before a retry and, after repeated DNS failures, could exhaust the four-send safety budget without a single real `stepOne` reaching the portal.

A transport failure with `SideEffectMayHaveOccurred == false` now cancels polling immediately, rolls back the reserved automatic attempt, and enters the pre-step retry backoff (`1, 2, 5, 15, 30, 60 s`). The pre-step failure streak is kept until there is positive evidence that a later `stepOne` reached the portal.

### 5. Terminal `stepOne` 4xx responses waited for polling that could not repair them

An explicit terminal 4xx (notably 429) previously stayed in the mailbox loop until the full polling schedule ended. These responses are now classified immediately. Ambiguous transport failures, 408, and 5xx still keep the mailbox schedule because a server-side code can be stronger evidence than a lost/browser-facing response.

### 6. Internet-confirmation offsets could serialize into tens of seconds

Post-`stepTwo` confirmation uses nominal absolute offsets `0..3500 ms`, but the previous implementation awaited each probe with its full 4 s timeout. If the probe endpoint stalled, eight checks could serialize into more than 30 seconds even though the captive authorization had already succeeded.

Each non-final probe is now capped by the time remaining until the next absolute offset. The last probe may still use the configured full timeout. This keeps a slow connectivity endpoint from turning a short confirmation schedule into a long blocking tail.

## Remaining timing risks / design decisions

### Early captive loss outside the 10-second guard

The largest remaining latency condition is intentional in the current architecture: **before `expiry - 10 s`, the agent does not test Internet and does not react to a captive state**. Therefore, if the portal invalidates the client substantially earlier than the predicted 24-hour boundary — for example because of a backend reset, a changed/randomized MAC, or another network-side event — the agent can remain offline until the predicted edge unless the user runs a manual connect.

This is not a C# migration regression; the PowerShell reference has the same policy. It is nevertheless a real field-risk because the observed ~24-hour lifetime is a scheduling prediction, not a formal SLA. Fixing it cleanly should be a separate product decision: preferably wake on a real WLAN/network-change event and perform a conservative two-probe captive confirmation on that edge, rather than polling the Internet continuously all day.

### Predicted expiry later/earlier than the real portal edge

The 10-second guard handles small skew well. Larger skew is not fully covered. If the portal remains authorized much longer than the predicted edge, repeated explicit `AlreadyAuthorized` responses eventually consume the configured automatic-send budget. Conversely, if the portal expires minutes early, the previous section applies.

A future hardening pass should make the predicted 24-hour timer one signal among two: the timer plus a low-cost network-edge signal. That avoids constant Internet polling while still recovering promptly from an unexpectedly early captive transition.

### Baseline latency is unavoidable but visible

The automatic trigger enters `AuthorizationFlow` and must complete one baseline API request before `stepOne`. This is required for freshness correctness. Normally the long-lived API `HttpClient` and ordinary system DNS keep it small, but a cold TLS connection or failing network can still add latency before `T=0` of the proven `stepOne`/polling schedule. The baseline itself should not be moved after or raced with `stepOne`.

## Validation available in this workspace

`python tests/static_checks.py` passes after the changes. A .NET SDK is not present in the Infra execution environment, so this audit did not rerun the C# build/contracts locally; the existing repository CI remains the compile/test gate.
