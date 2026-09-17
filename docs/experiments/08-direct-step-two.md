# Experiment 08 — direct `POST /stepTwo` without page GETs

Date: 2026-09-17

Goal: verify whether the browser-only page transitions can be removed from the working captive authorization flow.

The tested sequence was:

```text
baseline /pushmessages
→ POST /stepOne
→ scheduled GET /pushmessages
→ acquire fresh Wi-Fi code in memory
→ DIRECT POST /stepTwo
→ do not GET /stepTwo
→ do not GET /stepThree
→ verify Internet connectivity
```

No phone number, Wi-Fi code, Bearer, cookies, or other live credentials are stored in this document.

## Observed run

Baseline request succeeded with `Cache-Control: no-cache` and `X-Cache-Status: BYPASS`.

Polling used the previously selected absolute schedule from the start of `/stepOne`:

```text
100, 150, 200, 250, 350, 500, 700, 1000,
1400, 2000, 3000, 4500, 6500, 10000 ms
```

In the successful run:

```text
GET #1 start        ≈ 100.2 ms
fresh code observed ≈ 178.4 ms
GET #2 start        ≈ 150.2 ms
/stepOne observed   ≈ 207.4 ms
/stepOne response   = HTTP 302 -> stepTwo?...isMp=true

direct /stepTwo start ≈ 220.5 ms
direct /stepTwo done  ≈ 672.1 ms
/stepTwo RTT           ≈ 451.6 ms
/stepTwo response      = HTTP 302 -> stepThree
```

The code was already available in memory before the client observed completion of `/stepOne`.

Critically, the test made **no `GET /stepTwo`** before submitting the code. The direct form POST was accepted and returned the same success redirect previously observed in the browser flow:

```text
HTTP 302
Location: stepThree
```

The test then made **no `GET /stepThree`**. Internet connectivity was checked separately and was successfully confirmed. The first successful connectivity check in this run completed at about `2.87 s` from the start of `/stepOne`; this is only an upper observation point, not the exact moment when network access became active, because the connectivity request itself had non-zero latency.

## Conclusion

For the tested captive portal implementation, the minimal working protocol is experimentally confirmed as:

```text
baseline
→ POST /stepOne
→ GET /pushmessages until fresh code
→ direct POST /stepTwo
→ verify Internet
```

The following browser navigation requests are not required by the working client:

```text
GET /stepTwo
GET /stepThree
```

`stepThree` remains useful only as the redirect target that signals successful acceptance of `/stepTwo`; the client does not need to request that resource.

The next engineering step is to turn this verified path into an unattended MVP with captive detection, robust retry/error handling, IP-cache/DNS fallback, and result verification.
