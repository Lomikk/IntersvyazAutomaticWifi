#!/usr/bin/env node
'use strict';
// Offline-only. Reads an anonymized trace JSON; does NOT perform HTTP requests.
// Compare replay policies under explicit unknown-code-readiness scenarios.
const fs = require('fs');
const assert = require('assert');
const OFFSETS = [100,150,200,250,350,500,700,1000,1400,2000,3000,4500,6500,10000];
const STEP_MS = 200;
const LIMIT_MS = 12000;

function percentile(a, p) {
  if (!a.length) return null;
  const sorted = [...a].sort((x,y)=>x-y);
  return sorted[Math.max(0,Math.ceil(sorted.length*p)-1)];
}
function stats(values) {
  if (!values.length) return {n:0};
  return {n:values.length, min:Math.min(...values), median:percentile(values,.5),
    mean:values.reduce((s,x)=>s+x,0)/values.length,p90:percentile(values,.9),
    p95:percentile(values,.95),max:Math.max(...values)};
}
function observedCodePoll(a) {
  return a.polls.filter(p=>p.found).sort((x,y)=>x.completed_ms-y.completed_ms)[0];
}
function requestLatency(a) {
  const durations=a.polls.filter(p=>p.status===200&&Number.isFinite(p.completed_ms-p.started_ms))
    .map(p=>p.completed_ms-p.started_ms).filter(x=>x>0);
  return Math.max(15,Math.min(600,percentile(durations,.5)||60));
}
function policyReplay(kind, portalMs, readyMs, rttMs) {
  const launches=[];
  if (kind==='parallel') {
    for (const t of OFFSETS) if (t<=LIMIT_MS) launches.push(t);
  } else {
    const first = kind==='sequential' ? portalMs : Math.min(portalMs,250);
    let start=first;
    while (start<LIMIT_MS && launches.length<100) {
      launches.push(start);
      start+=Math.max(STEP_MS,rttMs); // never overlap; wait until previous response
    }
  }
  // Assume GET reads mailbox at mid-flight; misses and successes have same fixed
  // observed-per-attempt latency. No server-side rate limiting is simulated.
  let finish=null,requests=0;
  const completions=launches.map(start=>({
    start,done:start+rttMs,hasCode:start+rttMs/2>=readyMs
  }));
  const hits=completions.filter(x=>x.hasCode);
  finish=hits.length?Math.min(...hits.map(x=>x.done)):null;
  if (finish!==null) requests=completions.filter(x=>x.start<=finish).length;
  else requests=completions.length;
  return {finish,requests};
}
function compare(data) {
  const attempts=data.attempts.filter(a=>Number.isFinite(a.step_one_ms) && observedCodePoll(a));
  const real={
    total:attempts.length,
    codeBeforePortal:attempts.filter(a=>a.code_observed_ms<a.step_one_ms).length,
    foundGetStartBeforePortal:attempts.filter(a=>observedCodePoll(a).started_ms<a.step_one_ms).length,
    foundGetStartsBeforeEndsAfter:attempts.filter(a=>{const p=observedCodePoll(a);return p.started_ms<a.step_one_ms&&p.completed_ms>=a.step_one_ms}).length,
    smsGets:attempts.reduce((s,a)=>s+a.polls.length,0),
    http429:attempts.reduce((s,a)=>s+a.polls.filter(p=>p.status===429).length,0),
    codeObserved:stats(attempts.map(a=>a.code_observed_ms)),
    portalResponse:stats(attempts.map(a=>a.step_one_ms)),
    smsGetLatency:stats(attempts.flatMap(a=>a.polls.filter(p=>p.status===200).map(p=>p.completed_ms-p.started_ms))),
    first429ByIndex:attempts.flatMap(a=>a.polls.filter(p=>p.status===429).map(p=>p.index))
      .reduce((m,i)=>(m[i]=(m[i]||0)+1,m),{})
  };
  const sensitivity={};
  for (const [label,fraction] of [['early',0],['early10',.1],['early25',.25],['middle',.5],['late',1]]) {
    const comparisons=[],groups={parallel:[],sequential:[],hybrid:[]};
    for (const a of attempts) {
      const found=observedCodePoll(a);
      const ready=found.started_ms+(found.completed_ms-found.started_ms)*fraction;
      const rtt=requestLatency(a);
      const sims=Object.fromEntries(['parallel','sequential','hybrid']
        .map(k=>[k,policyReplay(k,a.step_one_ms,ready,rtt)]));
      if(Object.values(sims).some(x=>x.finish===null)) continue;
      const bias=sims.parallel.finish-a.code_observed_ms;
      comparisons.push({real:a.code_observed_ms,
        seqLag:sims.sequential.finish-sims.parallel.finish,
        hybridLag:sims.hybrid.finish-sims.parallel.finish,
        seqRequestsSaved:sims.parallel.requests-sims.sequential.requests,
        hybridRequestsSaved:sims.parallel.requests-sims.hybrid.requests,
        parallelModelError:bias});
      for (const key of Object.keys(groups))groups[key].push(sims[key]);
    }
    const delta=(key)=>stats(comparisons.map(x=>x[key]));
    sensitivity[label]={
      readyFraction:fraction,attempts:comparisons.length,
      parallel:{code:stats(groups.parallel.map(x=>x.finish)),requests:stats(groups.parallel.map(x=>x.requests))},
      sequential:{code:stats(groups.sequential.map(x=>x.finish)),requests:stats(groups.sequential.map(x=>x.requests)),
        delayComparedToParallel:delta('seqLag'),requestsSaved:delta('seqRequestsSaved'),
        fasterCount:comparisons.filter(x=>x.seqLag<0).length,
        slowerCount:comparisons.filter(x=>x.seqLag>0).length},
      hybrid:{code:stats(groups.hybrid.map(x=>x.finish)),requests:stats(groups.hybrid.map(x=>x.requests)),
        delayComparedToParallel:delta('hybridLag'),requestsSaved:delta('hybridRequestsSaved'),
        fasterCount:comparisons.filter(x=>x.hybridLag<0).length,
        slowerCount:comparisons.filter(x=>x.hybridLag>0).length},
      parallelBacktestError:delta('parallelModelError'),
    };
  }
  return {
    source:data.description,sourceSnapshot:data.snapshot_time,
    policy:{
      parallel:'fixed schedule from stepOne send at [100,150,200,250,350,500,700,1000,1400,2000,3000,4500,6500,10000] ms',
      sequential:'first GET at stepOne HTTP302, then serial GET every max(200ms, per-attempt observed RTT)',
      hybrid:'first GET on earlier of stepOne HTTP302 and 250ms fallback, then serial GET every max(200ms, per-attempt RTT)',
      readiness:'early/middle/late within real GET that found code; hypothetical, not measured',
      transport:'per-attempt median observed successful GET RTT, clamped 15..600 ms; code checked at request midpoint; no HTTP429 modeled',
      limitation:'Counterfactual HTTP responses and server arrival times unobservable. Changes in request timing may change server rate limits. This is scenario sensitivity, not A/B causality.'
    },
    real,sensitivity
  };
}
function selfTest(){
  assert.strictEqual(policyReplay('sequential',200,130,50).finish,250);
  assert.strictEqual(policyReplay('hybrid',600,150,50).finish,300); // fallback at 250
  assert.strictEqual(policyReplay('parallel',400,110,40).finish,140); // first 100ms poll
  assert.strictEqual(policyReplay('sequential',200,470,50).finish,650); // starts 200/400/600
  assert(percentile([10,20,30],.5)===20);
}
selfTest();
if(process.argv.includes('--self-test')){console.log('PASS: deterministic policy replay self-test');process.exit(0)}
const path=process.argv[2];
if(!path){console.error('Usage: node offline_sms_replay.cjs sanitized_traces.json');process.exit(2)}
const report=compare(JSON.parse(fs.readFileSync(path,'utf8')));
console.log(JSON.stringify(report,null,2));
