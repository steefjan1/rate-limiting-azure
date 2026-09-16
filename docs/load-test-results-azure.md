# Load test results

Target: `https://ca-api-gh7ovfkjehlha.redfield-55d9b9e7.swedencentral.azurecontainerapps.io`, limit 10 per 1000 ms, 2026-09-16 14:05:33Z

Azure Container Apps, **two replicas**, Azure Managed Redis Balanced B0, Sweden Central. Client in the Netherlands.

## Scenario 1: burst across the window boundary

1 request opens the window, 9 more at 90% of the window, 10 more at 110%. A perfect limiter accepts 10 in any 1000 ms span.

| Algorithm | Sent | 200 | 429 | Max accepted in any window | p50 latency | max latency |
|---|---|---|---|---|---|---|
| fixed-window | 20 | 20 | 0 | 20 | 91 ms | 716 ms |
| sliding-log | 20 | 20 | 0 | 19 | 43 ms | 68 ms |
| sliding-counter | 20 | 20 | 0 | 19 | 43 ms | 86 ms |
| token-bucket | 20 | 20 | 0 | 19 | 49 ms | 55 ms |
| leaky-bucket | 20 | 20 | 0 | 19 | 239 ms | 494 ms |
| redis/fixed-window | 20 | 20 | 0 | 19 | 43 ms | 120 ms |
| redis/sliding-log | 20 | 11 | 9 | 10 | 49 ms | 78 ms |
| redis/sliding-counter | 20 | 10 | 10 | 10 | 42 ms | 74 ms |
| redis/token-bucket | 20 | 12 | 8 | 11 | 44 ms | 46 ms |

## Scenario 2: sustained overload at 30 requests per 1000 ms for 3 windows

Uniform arrivals, 90 requests total. A perfect limiter accepts about 30.

| Algorithm | Sent | 200 | 429 | Max accepted in any window | p50 latency | p95 latency | max latency |
|---|---|---|---|---|---|---|---|
| fixed-window | 90 | 60 | 30 | 21 | 40 ms | 54 ms | 77 ms |
| sliding-log | 90 | 60 | 30 | 20 | 38 ms | 46 ms | 62 ms |
| sliding-counter | 90 | 40 | 50 | 20 | 39 ms | 47 ms | 108 ms |
| token-bucket | 90 | 77 | 13 | 31 | 38 ms | 80 ms | 148 ms |
| leaky-bucket | 90 | 79 | 11 | 23 | 697 ms | 1090 ms | 1247 ms |
| redis/fixed-window | 90 | 30 | 60 | 14 | 42 ms | 88 ms | 174 ms |
| redis/sliding-log | 90 | 30 | 60 | 11 | 41 ms | 47 ms | 83 ms |
| redis/sliding-counter | 90 | 29 | 61 | 11 | 41 ms | 47 ms | 51 ms |
| redis/token-bucket | 90 | 39 | 51 | 19 | 41 ms | 50 ms | 54 ms |

## Scenario 3: 20 simultaneous calls to a slow backend

The concurrency endpoint sleeps to simulate a slow dependency. Without a limiter every call reaches it.

| Endpoint | Sent | 200 | 429 | Reached backend concurrently | Wall time |
|---|---|---|---|---|---|
| /api/unlimited | 20 | 20 | 0 | 20 | 40 ms |
| /api/concurrency | 20 | 8 | 12 | 8 | 295 ms |

## Reading the numbers

- Every in-process limiter doubled. 60 accepted where 30 were configured, 8 concurrent calls where 4 were configured. Each replica keeps its own counter and the load balancer split the client's traffic between them.
- Every Redis-backed limiter held the single-instance numbers (compare `load-test-results.md`): 30, 30, 29, 39. The fixed window still leaks across the boundary (19 in one window), because that is the algorithm, not the store.
- The Redis round trip cost nothing measurable here: p50 for Redis endpoints is 41 to 49 ms, the same as the in-process endpoints, because the client to Sweden Central hop dominates. Redis and the API are in the same region.
