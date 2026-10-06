using System.Text;
using Microsoft.ML.Tokenizers;

namespace GraphReportChat.Api;

public static class InputTokens
{
    private static readonly Lazy<TiktokenTokenizer> Tokenizer = new(() => TiktokenTokenizer.CreateForEncoding("o200k_base"));
    public static int Count(string text) => Tokenizer.Value.CountTokens(text);
    public static string Excerpt(string text, int limit)
    {
        if (Count(text) <= limit) return text;
        const string marker = "\n[入力上限のため本文中間を省略]\n";
        int available = limit - Count(marker) - 8;
        while (available > 0)
        {
            int headTokens = available * 3 / 4;
            int head = Tokenizer.Value.GetIndexByTokenCount(text, headTokens, out _, out _);
            int tailStart = Tokenizer.Value.GetIndexByTokenCountFromEnd(text, available - headTokens, out _, out _);
            if (head > 0 && char.IsHighSurrogate(text[head - 1])) head--;
            if (tailStart < text.Length && tailStart > 0 && char.IsLowSurrogate(text[tailStart])) tailStart++;
            string candidate = text[..head] + marker + text[tailStart..];
            if (Count(candidate) <= limit) return candidate;
            available = available * 9 / 10;
        }
        return marker;
    }
}
