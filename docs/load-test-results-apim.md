# Load test results, through API Management

Target: `https://apim-gh7ovfkjehlha.azure-api.net/ratelimit`, limit 10 per 1000 ms, 2026-09-16 14:18:21Z

Same deployment as `load-test-results-azure.md` (Container Apps, two replicas, Azure Managed Redis), called through APIM Basic v2 with the `demo-client` subscription. APIM overrides `X-Client-Id` with the subscription id, so every row shares one partition; scenario 1 rows bleed into each other and are not comparable. Read scenarios 2 and 3.

## Scenario 2: sustained overload at 30 requests per 1000 ms for 3 windows

| Algorithm | Sent | 200 | 429 | other | Max accepted in any window | p50 latency | p95 latency | max latency |
|---|---|---|---|---|---|---|---|---|
| fixed-window | 90 | 60 | 30 | 0 | 21 | 40 ms | 52 ms | 79 ms |
| sliding-log | 90 | 58 | 32 | 0 | 20 | 41 ms | 51 ms | 65 ms |
| sliding-counter | 90 | 40 | 50 | 0 | 20 | 41 ms | 52 ms | 58 ms |
| token-bucket | 90 | 76 | 14 | 0 | 31 | 42 ms | 47 ms | 53 ms |
| leaky-bucket | 90 | 78 | 12 | 0 | 21 | 584 ms | 1044 ms | 1056 ms |
| redis/fixed-window | 90 | 30 | 60 | 0 | 11 | 43 ms | 56 ms | 82 ms |
| redis/sliding-log | 90 | 30 | 60 | 0 | 11 | 44 ms | 54 ms | 89 ms |
| redis/sliding-counter | 90 | 30 | 60 | 0 | 13 | 43 ms | 52 ms | 86 ms |
| redis/token-bucket | 90 | 39 | 51 | 0 | 19 | 44 ms | 54 ms | 108 ms |

## Scenario 3: 20 simultaneous calls to a slow backend

| Endpoint | Sent | 200 | 429 | other | Reached backend concurrently | Wall time |
|---|---|---|---|---|---|---|
| /api/unlimited | 20 | 20 | 0 | 0 | 20 | 62 ms |
| /api/concurrency | 20 | 8 | 12 | 0 | 8 | 325 ms |

## Reading the numbers

- Identical to the direct run within noise: in-process limiters still double (60, 58, 40, 76 accepted of 90; 8 concurrent instead of 4), Redis-backed limiters still hold the single-instance numbers (30, 30, 30, 39).
- The gateway added 2 to 5 ms at p50 (40 to 44 ms vs 38 to 42 ms direct).
- APIM rejected nothing itself. Its `rate-limit-by-key` is 600 per 60 s per subscription and the run made about 1,000 calls in 50 s, but `increment-condition` only counts responses below 400, so the ~360 service-layer 429s did not consume gateway budget and the ~640 accepted calls fit a v2 token bucket refilling at 600 per minute. Rejected calls not counting against the client is a deliberate choice in the policy, and it is why the gateway limit stayed out of the way.
- Every response carried `X-Gateway: apim`, and the body's `client` was `demo-client`, the subscription id APIM injected: gateway and service partitioned on the same identity.
- `X-RateLimit-Remaining` read 600 on consecutive calls. With `increment-condition`, APIM postpones the count to the end of the outbound pipeline, so the header reports remaining before the current call is added (documented; off by one for clients that read it literally).

## Single-call check

```
HTTP/1.1 200 OK
X-Gateway: apim
X-RateLimit-Remaining: 600

{"algorithm":"none","client":"demo-client","instance":"ca-api-gh7ovfkjehlha--azd-1789567443-bc6b749c6-n5bz9",...}
```
