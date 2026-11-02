# Estimated agent costs

Ordinary `LocalCustomAgent` and `LocalCustomHarnessAgent` runs automatically own a cost session when none is current; no manual pricing/session wiring is needed. `AgentCostTotals` remains an immutable result, not a price catalog. `AgentCostSession.Start()` copies the built-in USD catalog from `AgentModelPricing`. Estimates cover observed calls, not historical calls or an account-wide invoice.

## Dated USD catalog

Verified on **2026-10-25** against the official model pages; these are dated estimates, not live pricing or invoice guarantees. No network lookup occurs during accounting.

| Exact model ID | Input / 1M tokens | Cached input / 1M | Output / 1M |
|---|---:|---:|---:|
| `gpt-4o-mini` | $0.15 | $0.075 | $0.60 |
| `gpt-4o-mini-2024-07-18` (documented default snapshot) | $0.15 | $0.075 | $0.60 |
| `text-embedding-3-small` | $0.02 | Not claimed | $0 (no generated text) |

Sources: [GPT-4o mini](https://developers.openai.com/api/docs/models/gpt-4o-mini), [text-embedding-3-small](https://developers.openai.com/api/docs/models/text-embedding-3-small). The GPT-4o mini page marks the model deprecated; this feature does not upgrade models.

## Calculation and unknowns

For discounted chat input, decimal cost is `((input - cached) × inputRate + cached × cachedRate + output × outputRate) / 1,000,000`. Cache and reasoning counts are subsets, never extra billable tokens. Embedding cost is `input × 0.02 / 1,000,000`; vectors are not text output tokens.

- Response model ID wins over requested/configured ID. Matching is ordinal and exact; unknown snapshots/prefixes are not inferred.
- Missing or invalid discount-sensitive cache counts leave `EstimatedTotalCost` null. `KnownEstimatedCost` retains priceable components (for example, output), not a complete total.
- Missing/contradictory usage, unknown rates, in-flight work and overflow remain visibly unknown, not zero. Optional cache counts are not needed for embeddings because no cached tariff is claimed.
- Any explicit catalog, **including empty**, is authoritative: no default merge or fallback. Rates are privately copied; callers cannot mutate a running session's catalog.
- Null catalog selects defaults only for `USD`. Other currencies stay unpriced unless explicitly supplied rates are provided in that currency. No implicit FX conversion occurs.

## Automatic coverage and explicit boundaries

Tracked non-streaming chat and `CustomOpenAIClient.EmbedAsync` share one accumulator in an explicit session. Embeddings record SDK input/total usage exactly once at the awaited provider boundary, before model/vector validation. Failed or cancelled admitted attempts retain unknown usage/cost; local input/missing-client rejection does not invent a provider call. No payloads are retained.

With no ambient session, embeddings own a default session through the awaited call and validation. Disposal emits the existing numeric `agent.session.estimated_total_cost`, `known_estimated_cost`, completeness, unknown-count and currency fields when locally sampled. No exporter is installed by this feature.

All wrapper text/typed/structured overloads enter the central `AgentTelemetry.RunAsync` boundary. It starts an owned session before the wrapper span and disposes it after the awaited run and wrapper stop, including failure/cancellation. Nested wrappers and tool turns share the same current session: one aggregate, not one price per span. Existing caller sessions (including SearchEntries operations) remain current, undisposed and authoritative for catalog/currency. Independent concurrent runs remain isolated.

Final fake OTLP tests decode numeric costs, currency, completeness and counters through the real registered processors/exporter for ordinary plain/harness loops without manual sessions. Missing cache counts preserve numeric known output cost but omit the full total; unknown models and failed provider calls remain visibly incomplete.

Raw `GetChatClient` retains its identity and is untracked unless explicitly decorated. Direct `CreateAgent`/`CreateHarnessAgent` consumers, including JudgeAgent, require an explicit session for accounting; streaming remains untracked. These are not global invoice totals. Await all work before disposing explicit sessions; intentionally nested explicit sessions retain their existing independent attribution.
