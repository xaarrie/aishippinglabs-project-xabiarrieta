# Ranked fact search with PostgreSQL

Fact information search, research tools and chronological review search now use PostgreSQL full-text search with the fixed `simple` configuration. Only **Claim, EntityKey and EntityType** are searchable. Entry bodies, source quotes, clarification evidence and embeddings are excluded.

## Search behavior

| Contract | Behavior |
|---|---|
| Input | At most 300 characters; first 12 distinct lowercase letter/number terms of at least two characters, retaining combining marks. Punctuation separates terms. |
| Recall | Any term may match (OR); whole lexemes replace arbitrary substring matching. `alice` can match `Alice`; `alic` does not. |
| Languages | `simple` supports mixed-language lexical search without language-specific stemming. No typo tolerance, synonyms or automatic accent folding: `café` and `cafe` differ. |
| Relevance | PostgreSQL `ts_rank` descending, then entry number descending, then fact ID ascending. Frequency can affect rank; this is not the former matched-term/field-weight score. |
| Empty input | Review returns no hits; information/research validation rejects no-term input. No-term queries never become match-all queries. |
| Scope | Existing authorization, effective-fact rules, project/timeline boundaries, review chronology, result limits and evidence revalidation remain in place. |
| Evidence | Hit shape, provenance, fingerprints and citation checks are unchanged. Model/tool/search budgets remain 20/20/4. |

Terms are passed as parameters to `plainto_tsquery('simple', term)` and composed through the provider's tsquery OR API (`||`). Query-looking punctuation is not executable SQL or tsquery syntax. PostgreSQL's parser ultimately determines document lexemes; the input tokenizer is not a replacement for that parser.

## Index and query agreement

Migration `20261025163000_TimelineFactFullTextIndex` adds the GIN expression index `IX_TimelineEntryFacts_FullText` on `TimelineEntryFacts`:

```sql
to_tsvector('simple', "Claim" || ' ' || "EntityKey" || ' ' || "EntityType")
```

The runtime predicate uses this same expression with `@@`; ordering uses `ts_rank` on that vector and the same query. All three columns are required, so neither expression adds `COALESCE`. There is no stored/generated search column. Runtime model metadata, the frozen migration target and the snapshot describe the same provider expression index.

Production filtering, ranking, tie-breaks and limits stay in the translated database query. Only the explicitly identified EF InMemory provider uses an approximate regex-lexeme/matched-term-count seam for deterministic tests. That seam is **not PostgreSQL parser, rank or planner proof**; unsupported providers fail explicitly.

## Deployment and rollback

1. Have the database operator review the generated Up/Down script for this migration, following the project's normal migration process.
2. Schedule index creation appropriately: the ordinary transactional `CREATE INDEX` can block writes on a populated table. This change does not enable concurrent builds or startup migration automation.
3. Apply the migration through the approved deployment process before relying on indexed search performance. No fact data backfill or external provider call is required.
4. Roll back this migration to `20261018000300_FactInvalidationCause` to drop only the new index; fact rows and columns remain intact. Reverting search semantics requires reverting the application change separately.

No migration was executed as part of F1.

## Verification boundary

`TimelineFactFullTextSearchTests` checks all three generated SQL paths without opening a connection: parameterized plain queries, OR, `@@`, rank, deterministic ties, limits, eligibility/scope and exact agreement with the generated index expression. It also generates Up/Down scripts and checks frozen/runtime model consistency. InMemory tests cover lexical intent and retained evidence/access contracts, not actual PostgreSQL semantics.

`TimelineFactPostgresTests.Simple_FTS_lexemes_OR_rank_ties_chronology_and_citations` is an opt-in real-provider regression using the existing guarded disposable-database fixture. **It was not executed for F1.** That fixture creates, migrates and drops a dedicated database; the current task expressly prohibits those operations even if an opt-in flag already exists. Do not enable flags or use application connection settings to bypass that boundary.

Actual PostgreSQL lexeme/rank execution, index planner eligibility/selection and migration execution remain unverified. The focused offline filter does not run the PostgreSQL class or count guarded early returns as provider coverage. Next: independent F2 verification, followed by separately authorized database-operator validation.
