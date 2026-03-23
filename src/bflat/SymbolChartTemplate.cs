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

internal static partial class SymbolChartGenerator
{
    private static string BuildHtml(
        string binaryName,       // already HTML-escaped
        string binaryPath,       // already HTML-escaped
        string timestamp,
        string jsonData,         // raw JSON array – goes straight into <script>
        string fileSizeFmt,      // already HTML-escaped
        long   fileSizeBytes,
        string totalSizeFmt,     // already HTML-escaped
        ulong  totalSizeBytes,
        int    symbolCount,
        int    allCount,
        string largestSizeFmt,   // already HTML-escaped
        string largestNameHtml,  // already HTML-escaped
        string largestNameTitle, // already HTML-escaped (truncated)
        int    defaultTopN) => $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Symbol Map &#8211; {{binaryName}}</title>
<style>
:root {
  --bg:       #0d1117;
  --surface:  #161b22;
  --surface2: #21262d;
  --border:   #30363d;
  --text:     #c9d1d9;
  --muted:    #8b949e;
  --accent:   #58a6ff;
  --func:     #388bfd;
  --obj:      #3fb950;
  --notype:   #484f58;
  --tls:      #e3b341;
  --other:    #bc8cff;
}

* { box-sizing: border-box; margin: 0; padding: 0; }

body {
  background: var(--bg);
  color: var(--text);
  font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif;
  font-size: 14px;
  line-height: 1.5;
}

/* ── Header ───────────────────────────────────────────────────────────── */
.hdr {
  background: var(--surface);
  border-bottom: 1px solid var(--border);
  padding: 20px 32px;
  display: flex;
  align-items: center;
  gap: 16px;
}
.hdr-icon { font-size: 32px; }
.hdr h1   { font-size: 22px; font-weight: 700; color: #e6edf3; }
.hdr .sub { color: var(--muted); font-size: 13px; margin-top: 2px; }

/* ── Stats cards ──────────────────────────────────────────────────────── */
.stats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(190px, 1fr));
  gap: 16px;
  padding: 24px 32px;
}
.card {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 16px 20px;
}
.card .lbl {
  font-size: 11px;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: .06em;
  margin-bottom: 6px;
}
.card .val {
  font-size: 20px;
  font-weight: 700;
  color: var(--accent);
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.card .sub {
  font-size: 11px;
  color: var(--muted);
  margin-top: 4px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

/* ── Controls ─────────────────────────────────────────────────────────── */
.controls {
  padding: 0 32px 12px;
  display: flex;
  gap: 12px;
  align-items: center;
  flex-wrap: wrap;
}
input[type=text] {
  flex: 1;
  min-width: 220px;
  max-width: 440px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 8px 14px;
  color: var(--text);
  font-size: 14px;
  outline: none;
}
input[type=text]:focus { border-color: var(--accent); }
select {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 8px 12px;
  color: var(--text);
  font-size: 14px;
  outline: none;
  cursor: pointer;
}
.pills { display: flex; gap: 6px; flex-wrap: wrap; }
.pill {
  border: 1px solid var(--border);
  background: var(--surface2);
  border-radius: 20px;
  padding: 4px 14px;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  color: var(--muted);
  transition: all .15s;
  user-select: none;
}
.pill:hover        { color: var(--text); border-color: var(--muted); }
.pill.on           { color: #fff; }
.pill.on.all       { background: var(--accent); border-color: var(--accent); }
.pill.on.func      { background: var(--func);   border-color: var(--func);   }
.pill.on.obj       { background: var(--obj);    border-color: var(--obj);    }
.pill.on.tls       { background: var(--tls);    border-color: var(--tls); color: #000; }
.pill.on.other     { background: var(--other);  border-color: var(--other);  }

/* ── Table ────────────────────────────────────────────────────────────── */
.count   { padding: 6px 32px 10px; color: var(--muted); font-size: 12px; }
.tbl-wrap { padding: 0 32px 32px; overflow-x: auto; }

table { width: 100%; border-collapse: collapse; font-size: 13px; }

th {
  background: var(--surface);
  color: var(--muted);
  font-weight: 600;
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: .05em;
  padding: 10px 12px;
  text-align: left;
  border-bottom: 2px solid var(--border);
  white-space: nowrap;
  cursor: pointer;
  user-select: none;
  position: sticky;
  top: 0;
  z-index: 2;
}
th:hover        { color: var(--text); }
th.asc::after   { content: ' \25B2'; color: var(--accent); }
th.desc::after  { content: ' \25BC'; color: var(--accent); }

td {
  padding: 7px 12px;
  border-bottom: 1px solid #1c2128;
  vertical-align: middle;
}
tr:hover td { background: #161b22; }

/* rank column */
.r {
  color: var(--muted);
  font-variant-numeric: tabular-nums;
  width: 40px;
  text-align: right;
}

/* symbol name */
.nm {
  font-family: 'JetBrains Mono', 'Fira Code', Consolas, monospace;
  font-size: 11.5px;
  word-break: break-all;
  max-width: 500px;
}

/* type / bind badges */
.badge {
  display: inline-block;
  padding: 2px 8px;
  border-radius: 4px;
  font-size: 11px;
  font-weight: 600;
  font-family: monospace;
}
.bFUNC   { background: rgba(56,139,253,.15);  color: #79b8ff; border: 1px solid rgba(56,139,253,.3);  }
.bOBJECT { background: rgba(63,185,80,.15);   color: #56d364; border: 1px solid rgba(63,185,80,.3);   }
.bNOTYPE { background: rgba(110,118,129,.15); color: #8b949e; border: 1px solid rgba(110,118,129,.3); }
.bTLS    { background: rgba(227,179,65,.15);  color: #e3b341; border: 1px solid rgba(227,179,65,.3);  }
.bOTHER  { background: rgba(188,140,255,.15); color: #bc8cff; border: 1px solid rgba(188,140,255,.3); }
.bGLOBAL { color: var(--text); }
.bLOCAL  { color: var(--muted); font-style: italic; }
.bWEAK   { color: #e3b341; }

/* size / percent cells */
.sz { font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; font-family: monospace; }
.sz small { display: block; font-size: 10px; color: var(--muted); }
.pc { font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; color: var(--muted); font-size: 12px; }

/* bar chart cell */
.bc { width: 200px; }
.bw { width: 190px; height: 12px; background: var(--surface2); border-radius: 3px; overflow: hidden; margin: 2px 0; }
.bf { height: 100%; border-radius: 3px; min-width: 1px; transition: width .25s; }
.barFUNC   { background: linear-gradient(90deg, #1f6feb, #388bfd); }
.barOBJECT { background: linear-gradient(90deg, #238636, #3fb950); }
.barNOTYPE { background: #484f58; }
.barTLS    { background: linear-gradient(90deg, #9e6a03, #e3b341); }
.barOTHER  { background: linear-gradient(90deg, #6e40c9, #bc8cff); }

/* footer */
.footer { padding: 20px 32px; border-top: 1px solid var(--border); color: var(--muted); font-size: 12px; }
.footer code { color: var(--accent); font-size: 12px; }
</style>
</head>
<body>

<!-- ── Header ── -->
<div class="hdr">
  <div class="hdr-icon">&#x1F52C;</div>
  <div>
    <h1>Symbol Size Analysis</h1>
    <div class="sub">{{binaryName}} &bull; {{timestamp}}</div>
  </div>
</div>

<!-- ── Stats ── -->
<div class="stats">
  <div class="card">
    <div class="lbl">Binary File Size</div>
    <div class="val">{{fileSizeFmt}}</div>
    <div class="sub">{{fileSizeBytes:N0}} bytes on disk</div>
  </div>
  <div class="card">
    <div class="lbl">Total Symbol Size</div>
    <div class="val">{{totalSizeFmt}}</div>
    <div class="sub">{{totalSizeBytes:N0}} bytes &mdash; {{symbolCount:N0}} symbols</div>
  </div>
  <div class="card">
    <div class="lbl">Symbols With Size</div>
    <div class="val">{{symbolCount:N0}}</div>
    <div class="sub">out of {{allCount:N0}} total entries</div>
  </div>
  <div class="card">
    <div class="lbl">Largest Symbol</div>
    <div class="val">{{largestSizeFmt}}</div>
    <div class="sub" title="{{largestNameTitle}}">{{largestNameHtml}}</div>
  </div>
</div>

<!-- ── Controls ── -->
<div class="controls">
  <input type="text" id="q" placeholder="&#x1F50D;  Filter by symbol name&hellip;" oninput="render()" />
  <select id="topN" onchange="render()">
    <option value="50">Top 50</option>
    <option value="100">Top 100</option>
    <option value="200">Top 200</option>
    <option value="500">Top 500</option>
    <option value="0">All symbols</option>
  </select>
  <div class="pills">
    <span class="pill on all"  onclick="setType('all',this)">All</span>
    <span class="pill func"    onclick="setType('FUNC',this)">FUNC</span>
    <span class="pill obj"     onclick="setType('OBJECT',this)">OBJECT</span>
    <span class="pill other"   onclick="setType('NOTYPE',this)">NOTYPE</span>
    <span class="pill tls"     onclick="setType('TLS',this)">TLS</span>
  </div>
</div>

<div class="count" id="cnt"></div>

<!-- ── Table ── -->
<div class="tbl-wrap">
  <table>
    <thead>
      <tr>
        <th id="th-rank" onclick="chSort('rank')" class="desc">#</th>
        <th id="th-name" onclick="chSort('name')">Symbol Name</th>
        <th id="th-type" onclick="chSort('type')">Type</th>
        <th id="th-bind" onclick="chSort('bind')">Bind</th>
        <th id="th-size" onclick="chSort('size')">Size</th>
        <th id="th-pct"  onclick="chSort('pct')">% of Total</th>
        <th>Chart</th>
      </tr>
    </thead>
    <tbody id="tb"></tbody>
  </table>
</div>

<!-- ── Footer ── -->
<div class="footer">
  Generated by <strong>bflat</strong> symbol chart &bull; Binary: <code>{{binaryPath}}</code>
</div>

<script>
// Embedded symbol data – sorted by size descending, size > 0, section != UND
const D = {{jsonData}};
const MAX = D.length > 0 ? D[0].size : 1;
const TOT = D.reduce((a, s) => a + s.size, 0);

let sCol = 'size', sAsc = false, tFilt = 'all';

// ── Initialise the topN selector to match the value bflat chose ───────────
(function () {
  const sel = document.getElementById('topN');
  sel.value = String({{defaultTopN}});
  if (sel.selectedIndex < 0) sel.value = '100';
})();

// ── Helpers ───────────────────────────────────────────────────────────────

function fmt(n) {
  if (n >= 1048576) return (n / 1048576).toFixed(2) + ' MiB';
  if (n >= 1024)    return (n / 1024).toFixed(2) + ' KiB';
  return n + ' B';
}

function esc(s) {
  return s.replace(/&/g, '&amp;')
          .replace(/</g, '&lt;')
          .replace(/>/g, '&gt;')
          .replace(/"/g, '&quot;');
}

function badgeCls(t) {
  if (t === 'FUNC')   return 'bFUNC';
  if (t === 'OBJECT') return 'bOBJECT';
  if (t === 'NOTYPE') return 'bNOTYPE';
  if (t === 'TLS')    return 'bTLS';
  return 'bOTHER';
}

function barCls(t) {
  if (t === 'FUNC')   return 'barFUNC';
  if (t === 'OBJECT') return 'barOBJECT';
  if (t === 'NOTYPE') return 'barNOTYPE';
  if (t === 'TLS')    return 'barTLS';
  return 'barOTHER';
}

// ── Filter pill ───────────────────────────────────────────────────────────

function setType(t, el) {
  tFilt = t;
  document.querySelectorAll('.pill').forEach(p => p.classList.remove('on'));
  el.classList.add('on');
  render();
}

// ── Column sort ───────────────────────────────────────────────────────────

function chSort(col) {
  document.querySelectorAll('th[id^=th-]').forEach(h => h.className = '');
  if (sCol === col) {
    sAsc = !sAsc;
  } else {
    sCol = col;
    sAsc = (col === 'name' || col === 'type' || col === 'bind');
  }
  const th = document.getElementById('th-' + col);
  if (th) th.className = sAsc ? 'asc' : 'desc';
  render();
}

// ── Main render ───────────────────────────────────────────────────────────

function render() {
  const q    = document.getElementById('q').value.toLowerCase();
  let   topN = parseInt(document.getElementById('topN').value);
  if (isNaN(topN) || topN <= 0) topN = Infinity;

  let rows = D.filter(s => {
    if (tFilt !== 'all' && s.type !== tFilt) return false;
    if (q && !s.name.toLowerCase().includes(q)) return false;
    return true;
  });

  rows.sort((a, b) => {
    let av, bv;
    if      (sCol === 'name') { av = a.name; bv = b.name; }
    else if (sCol === 'type') { av = a.type; bv = b.type; }
    else if (sCol === 'bind') { av = a.bind; bv = b.bind; }
    else                      { av = a.size; bv = b.size; }
    if (av < bv) return sAsc ? -1 :  1;
    if (av > bv) return sAsc ?  1 : -1;
    return 0;
  });

  const total = rows.length;
  if (topN < Infinity) rows = rows.slice(0, topN);

  document.getElementById('cnt').textContent =
    'Showing ' + rows.length + ' of ' + total +
    ' matching symbols (' + D.length + ' total with size)';

  const html = rows.map((s, i) => {
    const pct  = (TOT > 0 ? (s.size / TOT * 100) : 0).toFixed(3);
    const barW = Math.max(1, Math.round(s.size / MAX * 100));
    return '<tr>' +
      '<td class="r">' + (i + 1) + '</td>' +
      '<td class="nm" title="' + esc(s.name) + '">' + esc(s.name) + '</td>' +
      '<td><span class="badge ' + badgeCls(s.type) + '">' + esc(s.type) + '</span></td>' +
      '<td><span class="badge b' + esc(s.bind) + '">' + esc(s.bind) + '</span></td>' +
      '<td class="sz">' + fmt(s.size) + '<small>' + s.size.toLocaleString() + ' B</small></td>' +
      '<td class="pc">' + pct + '%</td>' +
      '<td class="bc"><div class="bw"><div class="bf ' + barCls(s.type) + '" style="width:' + barW + '%"></div></div></td>' +
      '</tr>';
  }).join('');

  document.getElementById('tb').innerHTML = html ||
    '<tr><td colspan="7" style="text-align:center;padding:32px;color:var(--muted)">' +
    'No symbols match the current filter.</td></tr>';
}

render();
</script>
</body>
</html>
""";
}