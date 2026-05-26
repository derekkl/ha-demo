# ha-demo

Minimal .NET 8 app that demonstrates three OpenShift 4 HA primitives:

1. **Rolling updates with zero downtime** — `maxUnavailable: 0` keeps capacity at 100% throughout a rollout.
2. **Readiness probe gating** — pods receive no traffic until `/readyz` passes, simulating JIT/cache warmup.
3. **Liveness probe restart** — `/health/off` simulates a hung process; OpenShift kills and restarts the container.

---

## Deploy

### 1. Create the project

```bash
oc new-project ha-demo
```

### 2. Apply build resources (ImageStream + BuildConfig)

```bash
oc apply -f openshift/01-build.yaml
```

### 3. Build the image from source

```bash
oc start-build ha-demo --follow
```

The S2I build pulls `dotnet:8.0-ubi8` from the `openshift` namespace, compiles `src/`, and pushes to the `ha-demo:latest` ImageStreamTag.

### 4. Deploy

```bash
oc apply -f openshift/02-deploy.yaml
```

The `image.openshift.io/triggers` annotation on the Deployment automatically resolves the image from the ImageStream. Future builds trigger an automatic rollout.

### Verify

```bash
oc get pods -l app=ha-demo -w
oc get route ha-demo
```

---

## Demo 1 — Zero-downtime rolling update

**Goal:** show that `maxUnavailable: 0` keeps 100% capacity serving throughout a rollout.

Terminal 1 — start the traffic loop:

```bash
./scripts/demo-loop.sh
```

Terminal 2 — trigger a rollout:

```bash
oc rollout restart deployment/ha-demo
```

**What to watch:** `READY` stays `true` on every line. Hit counts gradually shift from the old pod name to the new one. Zero 503s or errors.

The rollout sequence is: bring up new pod → wait for readiness → only then terminate old pod. With `maxUnavailable: 0`, the old pod is never removed from the load balancer until its replacement is confirmed ready.

---

## Demo 2 — Readiness probe gating

**Goal:** show the readiness probe pulling a pod from rotation without a process crash.

With the loop running, manually flip one pod out of rotation:

```bash
curl -sk -X POST https://ha-demo.apps.uat-ocp4.uat.corp.cableone.net/ready/off
```

**What to watch:** within 4 s (failureThreshold 2 × period 2 s) that pod disappears from the loop output — 100% of traffic shifts to the remaining pod.

Restore:

```bash
curl -sk -X POST https://ha-demo.apps.uat-ocp4.uat.corp.cableone.net/ready/on
```

Traffic balances back as soon as `/readyz` passes again.

---

## Demo 3 — Liveness probe restart

**Goal:** show that a hung pod gets killed and restarted automatically — distinct from readiness, which only removes it from the load balancer.

With the loop running, break liveness on one pod:

```bash
curl -sk -X POST https://ha-demo.apps.uat-ocp4.uat.corp.cableone.net/health/off
```

In another terminal, watch pod state:

```bash
oc get pods -l app=ha-demo -w
```

**What to watch (timeline):**

| Time | Event |
|---|---|
| 0 s | `/healthz` starts returning 503 on the targeted pod |
| ~10 s | First liveness failure registered (initialDelay 5 s + first check) |
| ~30 s | `failureThreshold 3 × period 10 s` — OpenShift kills the container |
| ~32 s | Pod restarts; `STATUS` cycles `Running → Error → CrashLoopBackOff` then back to `Running` |
| ~42 s | Warmup completes, readiness passes, pod rejoins the load balancer |

The loop output will show traffic staying on the surviving pod throughout, then spreading back to both pods once the restarted one warms up.

There is no need to call `/health/on` — the restart resets all state. It is there only if you want to cancel the demo before the 30 s kill window.

---

## Demo 4 — OOM kill

**Goal:** show what happens when a pod exceeds its memory limit — the kernel OOM killer fires, OpenShift records `OOMKilled`, and the container restarts automatically.

With the loop running:

```bash
curl -sk -X POST https://ha-demo.apps.uat-ocp4.uat.corp.cableone.net/oom
```

Watch pods and memory in separate terminals:

```bash
oc get pods -l app=ha-demo -w
watch -n2 oc top pods -l app=ha-demo
```

**What to watch (timeline):**

| Time | Event |
|---|---|
| 0 s | Background allocator starts; 64 MiB chunks committed per iteration |
| ~4–8 s | RSS climbs past the 512 Mi cgroup limit |
| ~8 s | Kernel OOM killer fires — container exits with reason `OOMKilled` |
| ~10 s | Pod restarts; `oc describe pod <name>` shows `OOMKilled: true` and incremented restart count |
| ~20 s | Warmup completes, readiness passes, pod rejoins load balancer |

The loop stays on the surviving pod throughout, then rebalances once the restarted pod warms up — same observable behaviour as Demo 3 but the exit cause is different: `OOMKilled` vs `Error`.

Check the exit reason after the fact:

```bash
oc describe pod -l app=ha-demo | grep -A5 "Last State"
```

---

## HA Settings → Why it matters

| Setting | Value | Why it matters |
|---|---|---|
| `maxUnavailable` | `0` | Never terminates a pod until a replacement is ready — capacity stays at 100% during rollout |
| `maxSurge` | `1` | Allows one extra pod so the rollout can actually make progress when `maxUnavailable: 0` blocks removals |
| `terminationGracePeriodSeconds` | `30` | Gives in-flight requests time to complete before the container is killed |
| `preStop` sleep | `5 s` | Inserts a delay between `SIGTERM` and app shutdown so the router finishes deregistering the pod before it stops accepting connections |
| `readinessProbe` period / failure | `2 s / 2` | Detects a failed pod and pulls it from the load balancer within 4 s |
| `livenessProbe` period / failure | `10 s / 3` | Restarts genuinely stuck pods after 30 s without false-positive restarts during brief GC pauses |
| `WARMUP_SECONDS` | `10` (default) | Simulates JIT / cache warmup — pod receives zero traffic until it self-reports ready |
| `/ready/off` · `/ready/on` | — | Ad-hoc readiness toggle for live demos without needing to crash the process |

---

## Endpoints

| Method | Path | Description |
|---|---|---|
| `GET` | `/` | `{ pod, count, uptime, ready }` |
| `GET` | `/healthz` | Liveness — `200` normally, `503` when manually toggled off |
| `GET` | `/readyz` | Readiness — `503` during warmup or when manually toggled off, `200` otherwise |
| `POST` | `/ready/off` | Manually mark pod not-ready (pulled from LB within 4 s) |
| `POST` | `/ready/on` | Restore readiness |
| `POST` | `/health/off` | Simulate hung process — pod killed after 30 s (3 × 10 s) |
| `POST` | `/health/on` | Cancel liveness failure before the kill window |
| `POST` | `/oom` | Allocate memory until OOM kill — container restarts with `OOMKilled` exit reason |
