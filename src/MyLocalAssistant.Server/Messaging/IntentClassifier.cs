namespace MyLocalAssistant.Server.Messaging;

/// <summary>
/// Classifies inbound messages to a skill id using a two-stage approach:
/// 1. Fast keyword matching (no LLM cost).
/// 2. If ambiguous, falls back to returning null so the caller can do a full LLM intent pass.
///
/// Keyword maps are bilingual (EN + TR) to support the Turkish manufacturing market.
/// </summary>
public static class IntentClassifier
{
    private static readonly (string[] Keywords, string SkillId, int Priority)[] s_rules =
    [
        // shortage / eksiklik
        (["shortage", "shortages", "missing part", "missing material",
          "eksiklik", "eksik malzeme", "eksik parça", "stok yok",
          "material shortage", "stock out", "stockout"],
         "shortage-review", 10),

        // purchase request / satın alma talebi
        (["purchase request", "pr request", "buy ", "order request", "requisition",
          "satın alma talebi", "satın alma isteği", "sipariş talebi", "malzeme talebi",
          "i need to order", "we need to buy", "please order"],
         "purchase-request", 10),

        // supplier follow-up / tedarikçi takibi
        (["follow up", "followup", "follow-up", "overdue", "late delivery", "delayed order",
          "geç teslimat", "gecikme", "tedarikçi takip", "po follow", "chase supplier",
          "supplier delay", "delivery delay", "where is my order"],
         "supplier-followup", 10),

        // python / script / code tasks
        (["python", "script", "write a script", "run a script", "parse file", "read file",
          "read csv", "read json", "read excel", "parse csv", "parse json",
          "calculate", "analyse data", "analyze data", "process file", "extract from file",
          "kod yaz", "python kodu", "dosyayı oku", "veri analizi"],
         "python-script", 8),
    ];

    /// <summary>
    /// Returns the matched skill id, or null if no confident match.
    /// </summary>
    public static string? Classify(string text)
    {
        var lower = text.ToLowerInvariant();

        string? best      = null;
        int     bestScore = 0;

        foreach (var (keywords, skillId, priority) in s_rules)
        {
            int hits = keywords.Count(k => lower.Contains(k));
            if (hits == 0) continue;

            int score = hits * priority;
            if (score > bestScore)
            {
                bestScore = score;
                best      = skillId;
            }
        }

        return best;
    }
}
