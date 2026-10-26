using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
using ProjectTimeline.DataAccessLogic;

namespace ProjectTimeline.BusinessLogic;

/// <summary>Fact-only FTS. Callers retain eligibility, authorization, tie-breaks and limits.</summary>
internal static class TimelineFactFullTextSearch
{
    internal static string[] Terms(string text)
    {
        if (text is null || text.Length > 300) throw new ArgumentException("Search text must be at most 300 characters.");
        return Lexemes(text).Where(x => x.Length >= 2).Distinct(StringComparer.Ordinal).Take(12).ToArray();
    }

    private static IEnumerable<string> Lexemes(string text) => Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}][\p{L}\p{N}\p{M}]*")
        .Select(x => x.Value);

    internal static IOrderedQueryable<T> Rank<T>(ProjectTimelineDbContext db, IQueryable<T> source,
        Expression<Func<T, TimelineFact>> fact, string[] terms)
    {
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            // Explicit test seam only: regex lexemes and matched-term count approximate, NOT PostgreSQL's parser/rank.
            Expression<Func<TimelineFact, float>> approximation = f => ApproximateScore(f.Claim, f.EntityKey, f.EntityType, terms);
            var score = Expression.Lambda<Func<T, float>>(Substitute(approximation.Body, approximation.Parameters[0], fact.Body), fact.Parameters);
            var positive = Expression.Lambda<Func<T, bool>>(Expression.GreaterThan(score.Body, Expression.Constant(0f)), fact.Parameters);
            return source.Where(positive).OrderByDescending(score);
        }
        if (!db.Database.IsNpgsql()) throw new NotSupportedException("Fact full-text search requires PostgreSQL.");
        if (terms.Length == 0) return source.Where(_ => false).OrderBy(_ => 0);

        // Compose supported tsquery OR operations; every untrusted term remains a captured parameter.
        Expression? query = null;
        foreach (var term in terms)
        {
            var word = term;
            Expression<Func<NpgsqlTsQuery>> plain = () => EF.Functions.PlainToTsQuery("simple", word);
            query = query is null ? plain.Body : Expression.Call(typeof(NpgsqlFullTextSearchLinqExtensions),
                nameof(NpgsqlFullTextSearchLinqExtensions.Or), null, query, plain.Body);
        }
        // Required columns: no COALESCE. Keep order/concatenation identical to the provider expression index.
        Expression<Func<TimelineFact, NpgsqlTsVector>> vector = f => EF.Functions.ToTsVector("simple",
            f.Claim + " " + f.EntityKey + " " + f.EntityType);
        var document = Substitute(vector.Body, vector.Parameters[0], fact.Body);
        var matches = Expression.Call(typeof(NpgsqlFullTextSearchLinqExtensions), nameof(NpgsqlFullTextSearchLinqExtensions.Matches),
            null, document, query!);
        var rank = Expression.Call(typeof(NpgsqlFullTextSearchLinqExtensions), nameof(NpgsqlFullTextSearchLinqExtensions.Rank),
            null, document, query!);
        return source.Where(Expression.Lambda<Func<T, bool>>(matches, fact.Parameters))
            .OrderByDescending(Expression.Lambda<Func<T, float>>(rank, fact.Parameters));
    }

    private static float ApproximateScore(string claim, string key, string type, string[] terms)
    {
        var words = Lexemes(claim + " " + key + " " + type).ToHashSet(StringComparer.Ordinal);
        return terms.Count(words.Contains);
    }

    private static Expression Substitute(Expression body, ParameterExpression parameter, Expression value)
        => new Replace(parameter, value).Visit(body)!;
    private sealed class Replace(ParameterExpression parameter, Expression value) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parameter ? value : node;
    }
}
