// bflat C# compiler
// Copyright (C) 2026 Demerzel Solutions Limited (Nethermind)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

/// <summary>
/// Represents a single entry from an ELF symbol table.
/// </summary>
internal record ElfSymbol(
    int     Ordinal,
    ulong   Address,
    ulong   Size,
    string  Type,         // FUNC, OBJECT, NOTYPE, SECTION, FILE, COMMON, TLS, …
    string  Bind,         // LOCAL, GLOBAL, WEAK
    string  Visibility,   // DEFAULT, PROTECTED, HIDDEN, INTERNAL
    string  SectionIndex, // number, UND, ABS, COM, …
    string  Name
);

// ---------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------

/// <summary>
/// Parses the text output produced by <c>readelf -sW</c> (or <c>llvm-readelf -sW</c>).
/// </summary>
internal static class ElfSymbolParser
{
    /// <summary>
    /// Parse the full stdout of <c>readelf -sW &lt;binary&gt;</c> and return all
    /// symbol entries found across every symbol table section.
    /// </summary>
    public static List<ElfSymbol> Parse(string readelfOutput)
    {
        var symbols = new List<ElfSymbol>();
        bool inTable = false;

        foreach (var rawLine in readelfOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // Detect the header row that precedes data rows:
            //    Num:    Value          Size Type    Bind   Vis      Ndx Name
            if (line.TrimStart().StartsWith("Num:", StringComparison.Ordinal)
                && line.Contains("Value", StringComparison.Ordinal)
                && line.Contains("Name", StringComparison.Ordinal))
            {
                inTable = true;
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.TrimStart().StartsWith("Symbol table", StringComparison.OrdinalIgnoreCase))
            {
                inTable = false;
                continue;
            }

            if (!inTable) continue;

            // Data row format (space-separated, >= 7 tokens, name may be absent):
            //   N: ADDR SIZE TYPE BIND VIS NDX [name...]
            // where N ends with ':'
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7) continue;

            if (!parts[0].EndsWith(':')) continue;
            if (!int.TryParse(parts[0].TrimEnd(':'), out int ordinal)) continue;

            if (!ulong.TryParse(parts[1],
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out ulong address))
                continue;

            if (!ulong.TryParse(parts[2],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out ulong size))
                continue;

            string type = parts[3];
            string bind = parts[4];
            string vis  = parts[5];
            string ndx  = parts[6];
            string name = parts.Length > 7
                ? string.Join(" ", parts, 7, parts.Length - 7)
                : string.Empty;

            // Strip version suffix, e.g. "printf@GLIBC_2.5"
            int atIdx = name.IndexOf('@');
            if (atIdx > 0) name = name[..atIdx];

            symbols.Add(new ElfSymbol(ordinal, address, size, type, bind, vis, ndx, name));
        }

        return symbols;
    }
}

// ---------------------------------------------------------------------------
// Generator  (HTML template lives in SymbolChartTemplate.cs)
// ---------------------------------------------------------------------------

/// <summary>
/// Generates a self-contained, interactive HTML page that visualises the
/// sizes of ELF symbols as horizontal bar charts.
/// <para>
/// The actual HTML template is defined as <c>BuildHtml</c> in
/// <c>SymbolChartTemplate.cs</c> so that markup / CSS / JS can be edited
/// without touching the data-processing logic here.
/// </para>
/// </summary>
internal static partial class SymbolChartGenerator
{
    /// <summary>
    /// Build and write the HTML report.
    /// </summary>
    /// <param name="outputHtmlPath">Destination .html file.</param>
    /// <param name="binaryPath">Path to the analysed ELF binary (for display).</param>
    /// <param name="allSymbols">Full list returned by <see cref="ElfSymbolParser.Parse"/>.</param>
    /// <param name="defaultTopN">How many symbols to show by default.</param>
    public static void Generate(
        string                   outputHtmlPath,
        string                   binaryPath,
        IReadOnlyList<ElfSymbol> allSymbols,
        int                      defaultTopN = 100)
    {
        // ── Filter & sort ──────────────────────────────────────────────────
        var significant = allSymbols
            .Where(s => s.Size > 0
                     && s.SectionIndex != "UND"
                     && !string.IsNullOrWhiteSpace(s.Name))
            .OrderByDescending(s => s.Size)
            .ToList();

        ulong totalSize = significant.Aggregate(0UL, (acc, s) => acc + s.Size);
        long  fileSize  = File.Exists(binaryPath) ? new FileInfo(binaryPath).Length : 0;

        string binaryName     = Path.GetFileName(binaryPath);
        string timestamp      = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string largestSizeFmt = significant.Count > 0 ? Fmt((long)significant[0].Size) : "—";
        string largestName    = significant.Count > 0 ? significant[0].Name : "—";

        // ── Serialise symbols to JSON (embedded in HTML <script>) ──────────
        var jsonArray = new StringBuilder();
        jsonArray.Append('[');
        for (int i = 0; i < significant.Count; i++)
        {
            var s = significant[i];
            if (i > 0) jsonArray.Append(',');
            jsonArray.Append('{');
            jsonArray.Append($"\"name\":{JsonEscape(s.Name)},");
            jsonArray.Append($"\"size\":{s.Size},");
            jsonArray.Append($"\"type\":{JsonEscape(s.Type)},");
            jsonArray.Append($"\"bind\":{JsonEscape(s.Bind)},");
            jsonArray.Append($"\"section\":{JsonEscape(s.SectionIndex)}");
            jsonArray.Append('}');
        }
        jsonArray.Append(']');

        // ── Delegate HTML rendering to the template ────────────────────────
        string html = BuildHtml(
            binaryName:      HtmlEscape(binaryName),
            binaryPath:      HtmlEscape(binaryPath),
            timestamp:       timestamp,
            jsonData:        jsonArray.ToString(),
            fileSizeFmt:     HtmlEscape(Fmt(fileSize)),
            fileSizeBytes:   fileSize,
            totalSizeFmt:    HtmlEscape(Fmt((long)totalSize)),
            totalSizeBytes:  totalSize,
            symbolCount:     significant.Count,
            allCount:        allSymbols.Count,
            largestSizeFmt:  HtmlEscape(largestSizeFmt),
            largestNameHtml: HtmlEscape(largestName),
            largestNameTitle:HtmlEscape(TruncateName(largestName, 60)),
            defaultTopN:     defaultTopN);

        File.WriteAllText(outputHtmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // ── Internal helpers ────────────────────────────────────────────────────

    internal static string Fmt(long n)
    {
        if (n >= 1_048_576) return $"{n / 1_048_576.0:F2} MiB";
        if (n >= 1_024)     return $"{n / 1_024.0:F2} KiB";
        return $"{n} B";
    }

    internal static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");

    internal static string JsonEscape(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20)
                        sb.Append($"\\u{(int)c:x4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    internal static string TruncateName(string name, int max) =>
        name.Length <= max ? name : name[..max] + "…";
}