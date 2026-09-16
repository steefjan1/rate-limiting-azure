# Load test results

Target: `http://localhost:5080`, limit 10 per 1000 ms, 2026-09-16 12:45:52Z

## Scenario 1: burst across the window boundary

1 request opens the window, 9 more at 90% of the window, 10 more at 110%. A perfect limiter accepts 10 in any 1000 ms span.

| Algorithm | Sent | 200 | 429 | Max accepted in any window | p50 latency | max latency |
|---|---|---|---|---|---|---|
| fixed-window | 20 | 20 | 0 | 19 | 2 ms | 51 ms |
| sliding-log | 20 | 11 | 9 | 10 | 4 ms | 8 ms |
| sliding-counter | 20 | 10 | 10 | 10 | 1 ms | 5 ms |
| token-bucket | 20 | 19 | 1 | 18 | 2 ms | 3 ms |
| leaky-bucket | 20 | 20 | 0 | 11 | 402 ms | 999 ms |

## Scenario 2: sustained overload at 30 requests per 1000 ms for 3 windows

Uniform arrivals, 90 requests total. A perfect limiter accepts about 30.

| Algorithm | Sent | 200 | 429 | Max accepted in any window | p50 latency | p95 latency | max latency |
|---|---|---|---|---|---|---|---|
| fixed-window | 90 | 30 | 60 | 11 | 1 ms | 3 ms | 5 ms |
| sliding-log | 90 | 30 | 60 | 11 | 1 ms | 1 ms | 3 ms |
| sliding-counter | 90 | 26 | 64 | 10 | 0 ms | 1 ms | 4 ms |
| token-bucket | 90 | 39 | 51 | 19 | 0 ms | 1 ms | 3 ms |
| leaky-bucket | 90 | 40 | 50 | 11 | 1 ms | 1002 ms | 1004 ms |

## Scenario 3: 20 simultaneous calls to a slow backend

The concurrency endpoint sleeps to simulate a slow dependency. Without a limiter every call reaches it.

| Endpoint | Sent | 200 | 429 | Reached backend concurrently | Wall time |
|---|---|---|---|---|---|
| /api/unlimited | 20 | 20 | 0 | 20 | 2 ms |
| /api/concurrency | 20 | 4 | 16 | 4 | 256 ms |
