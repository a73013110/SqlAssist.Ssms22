// 操作畫面使用共用殼層，SQL 內容只讀產品產生器輸出的資料。
const demoData = window.featureDemoData;

function generatedSql(text) {
  const comment = text.indexOf('--');
  if (comment >= 0) {
    return generatedSql(text.slice(0, comment)) + `<span class="comment">${esc(text.slice(comment))}</span>`;
  }
  return sql(text).replace(/(?<![\w>])(SET|ON|ALTER|ADD|PRIMARY|CLUSTERED|NONCLUSTERED|INDEX|FOREIGN|REFERENCES|EXEC|IF|BEGIN|END|INSERT|INTO|VALUES|IN|EXISTS|DECLARE|OUTPUT|MERGE|USING|WHEN|MATCHED|TARGET|THEN|UPDATE|AND|PROCEDURE|FUNCTION|RETURNS|RETURN|COUNT)(?![\w<])/g,
    '<span class="kw">$1</span>');
}

function generatedLines(text, selection = '') {
  return text.trimEnd().split('\n').map((line, i) => {
    let content = generatedSql(line);
    if (selection) content = content.replace(selection, `<span class="field-selection">${selection}</span>`);
    return `<div class="line" data-n="${i + 1}">${content || ' '}</div>`;
  }).join('');
}

function contextMenu(items, active, location = '') {
  return `<div class="context-menu ${location}">${items.map((item, i) => item === '-'
    ? '<hr>' : `<div class="context-item ${i === active ? 'active' : ''}">${item}</div>`).join('')}</div>`;
}

function definitionDemo(s) {
  if (!s.open) {
    const body = `<div class="editor"><div class="line" data-n="1">${sql('SELECT * FROM dbo.')}Lo<span class="caret"></span>an;</div></div>`;
    return shell('Loan.sql', body, { key: s.key, pointer: s.pointer });
  }
  const body = `<div class="code-scroll" data-scroll="${s.scroll || 0}">${generatedLines(demoData.definition)}</div>`;
  return shell('SQLQuery2.sql', body, { extra: 'Loan.sql', key: s.key, pointer: s.pointer });
}

function surroundDemo(s) {
  if (s.applied) {
    const text = s.edited ? demoData.surround.replace('1 = 1', 'EXISTS (SELECT 1 FROM dbo.Loan)') : demoData.surround;
    let body = `<div class="editor">${generatedLines(text, s.edited || s.done ? '' : '1 = 1')}`;
    if (s.done) body = body.replace(/END<\/span>/, 'END</span><span class="caret"></span>');
    return shell('Lib_Reader.sql', body + '</div>', { key: s.key });
  }
  let body = `<div class="editor">${demoData.selection.split('\n').map((line, i) =>
    `<div class="line" data-n="${i + 1}"><span class="selected-sql">${sql(line)}</span></div>`).join('')}</div>`;
  if (s.menu) {
    body += `<div style="position:absolute;left:310px;top:64px">${contextMenu([
      '剪下 <small>Ctrl+X</small>', '複製 <small>Ctrl+C</small>', '貼上 <small>Ctrl+V</small>', '-',
      '以片段包住選取範圍', '新增至收藏…', '程式碼片段…'
    ], 4)}</div>`;
  }
  if (s.picker) {
    const filtered = s.typed === 'ifb';
    const list = filtered ? demoData.snippets.filter(item => item.shortcut === 'ifb') : demoData.snippets;
    const before = demoData.surround.slice(0, demoData.surroundOffset);
    const original = demoData.surround.slice(demoData.surroundOffset, demoData.surroundOffset + demoData.surroundLength);
    const after = demoData.surround.slice(demoData.surroundOffset + demoData.surroundLength);
    const highlightedOriginal = original.split('\n').map(line => {
      const indent = line.match(/^ */)[0];
      return indent + `<span class="surround-original">${esc(line.slice(indent.length))}</span>`;
    }).join('\n');
    body += `<div class="surround-picker">
      <div class="selection-summary">包住 2 行 · ${demoData.selection.length} 個字元，不會執行 SQL</div>
      <div class="searchbox">${s.typed || ''}<span class="caret"></span></div>
      <div class="count">${list.length} / ${demoData.snippets.length} 個可包夾片段</div>
      <div class="surround-columns"><div class="snippet-list">${list.map(item =>
        `<div class="snippet-item ${item.shortcut === 'ifb' ? 'active' : ''}">${pill(item.shortcut)}${esc(item.title)}<div class="snippet-description">${esc(item.description)}</div></div>`).join('')}</div>
        <div><div style="font-size:14px;color:#666">套用後以 Tab 填寫 1 個欄位</div><div class="surround-preview">${esc(before)}${highlightedOriginal}${esc(after)}</div></div>
      </div>
      <div class="picker-footer">↑↓ 選擇 · Enter 套用 · Esc 取消<span class="grow"></span>取消<span class="apply">套用</span></div>
    </div>`;
  }
  return shell('Lib_Reader.sql', body, s);
}

function insertDemo(s) {
  if (s.expanded) {
    const text = s.filled ? demoData.insert.replace("N''", "N'SQL'") : demoData.insert;
    const body = `<div class="editor insert-editor">${generatedLines(text)}</div>`;
    return shell('Lib_Tag.sql', body, { key: s.key });
  }
  let body = `<div class="editor"><div class="line" data-n="1">${generatedSql('INSERT INTO dbo.' + (s.typed || ''))}<span class="caret"></span></div></div>`;
  if (s.typed) body += `<div class="completion insert-popup"><div class="suggest selected">${ico('table')}Lib_Tag<small>Table · dbo</small></div><div class="suggest-foot">${ico('table')}</div></div>`;
  return shell('Lib_Tag.sql', body, { key: s.key });
}

const moduleIcon = kind => `<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="3" width="18" height="18" rx="1"/><path d="M7 8h10M7 12h10M7 16h6"/>${kind === 'function' ? '<path d="M15 16h3"/>' : ''}</svg>`;

function statementDemo(s, { tab, prefix, name, kind, result }) {
  if (s.expanded) {
    const body = `<div class="code-scroll expansion-code" data-scroll="${s.scroll || 0}">${generatedLines(demoData[result])}</div>`;
    return shell(tab, body, { key: s.key });
  }
  const body = `<div class="editor"><div class="line" data-n="1">${generatedSql(prefix + (s.typed || ''))}<span class="caret"></span></div></div>
    ${s.typed ? `<div class="completion statement-popup"><div class="suggest selected">${kind === 'table' ? ico('table') : moduleIcon(kind)}<span>${name}</span><small>${kind === 'table' ? 'Table' : kind === 'function' ? 'Function' : 'Procedure'} · dbo</small></div><div class="suggest-foot">${kind === 'table' ? ico('table') : moduleIcon(kind)}</div></div>` : ''}`;
  return shell(tab, body, { key: s.key });
}

const executeDemo = s => statementDemo(s, {
  tab: 'Loan.sql', prefix: 'EXEC dbo.', name: 'usp_Loan_Count', kind: 'procedure', result: 'execute'
});
const mergeDemo = s => statementDemo(s, {
  tab: 'Cat_BookCopy.sql', prefix: 'MERGE INTO dbo.', name: 'Cat_BookCopy', kind: 'table', result: 'merge'
});
const alterProcedureDemo = s => statementDemo(s, {
  tab: 'usp_Loan_Count.sql', prefix: 'ALTER PROCEDURE dbo.', name: 'usp_Loan_Count',
  kind: 'procedure', result: 'alterProcedure'
});
const alterFunctionDemo = s => statementDemo(s, {
  tab: 'fn_LoanCount.sql', prefix: 'ALTER FUNCTION dbo.', name: 'fn_LoanCount',
  kind: 'function', result: 'alterFunction'
});

function gridDemo(s) {
  if (s.pasteView) {
    const query = 'SELECT *\nFROM dbo.Loan\nWHERE ';
    const text = s.pasted ? query + '\n' + demoData.predicate.replaceAll('\r\n', '\n') : query;
    const code = s.pasted ? generatedLines(text) : generatedLines(text).replace(/<\/div>$/, ' <span class="caret"></span></div>');
    const body = `<div class="editor paste-editor">${code}</div>`;
    return shell('SQLQuery2.sql', body, { extra: 'Loan.sql', key: s.key });
  }
  const rows = demoData.gridValues;
  let body = `<div class="editor grid-editor">${generatedLines('SELECT CopyNo, ReaderId\nFROM dbo.Loan;')}</div>
    <div class="grid-panel"><div class="grid-tabs">${ico('table')} 結果　<span style="color:#777">訊息</span></div>
    <table class="grid-table"><thead><tr><th></th><th>CopyNo</th><th>ReaderId</th></tr></thead><tbody>${rows.map((value, i) =>
      `<tr><td class="row-number">${i + 1}</td><td class="${s.selected && i < 3 ? 'chosen' : ''}">${value}</td><td>${[101, 102, 103, 104][i]}</td></tr>`).join('')}</tbody></table></div>`;
  if (s.menu) body += contextMenu([
    '複製 <small>Ctrl+C</small>', '另存結果為…', '-', '複製成 IN 條件',
    '複製成 Markdown 表格', '複製成 JSON', '-', '建立 #temp 指令碼…', '欄位剖析…', '檢視這一格完整內容…'
  ], 3, 'grid-menu');
  return shell('Loan.sql', body, { ...s, status: s.copied ? '已複製 IN 條件' : '查詢已成功執行' });
}

Object.assign(demos, {
  'structure-preview': {
    title: '建議清單預覽物件結構',
    caption: '輸入 libr → 選中 Lib_Reader → 按 → 展開下方結構預覽 → 點「指令碼」查看完整資料表 DDL，全程留在目前查詢視窗。',
    draw: completion, poster: 9,
    frames: [
      scene(1000, {}), scene(250, { typed: 'l' }), scene(250, { typed: 'li' }),
      scene(250, { typed: 'lib' }), scene(1400, { typed: 'libr' }),
      scene(650, { typed: 'libr', key: '→' }),
      scene(1700, { typed: 'libr', preview: true }),
      scene(700, { typed: 'libr', preview: true, pointer: [255, 362] }),
      scene(350, { typed: 'libr', preview: true, pointer: [255, 362], click: true }),
      scene(3400, { typed: 'libr', preview: true, script: true })
    ]
  },
  'f12-definition': {
    title: 'F12 開啟資料表 DDL',
    caption: '游標放在 Loan → 按 F12 → 新查詢顯示欄位、主鍵、索引、外來鍵與物件說明。本例開啟編輯器自動換行；不執行 DDL。',
    draw: definitionDemo, poster: 8,
    frames: [
      scene(1400, {}), scene(650, { key: 'F12' }),
      scene(1100, { open: true, key: 'F12' }), scene(2400, { open: true }),
      scene(100, { open: true, scroll: 60, pointer: [1045, 505] }),
      scene(100, { open: true, scroll: 120, pointer: [1045, 505] }),
      scene(100, { open: true, scroll: 180, pointer: [1045, 505] }),
      scene(600, { open: true, scroll: 240, pointer: [1045, 505] }),
      scene(4000, { open: true, scroll: 300 })
    ]
  },
  'surround-snippet': {
    title: '右鍵以片段包住 SQL',
    caption: '選取 SQL → 右鍵「以片段包住選取範圍」→ 搜尋 ifb → 檢查預覽 → 套用 → 填寫 IF 條件 → Tab 結束欄位導航。',
    draw: surroundDemo, poster: 5,
    frames: [
      scene(1400, { pointer: [311, 147] }),
      scene(350, { pointer: [311, 147], click: true, key: '右鍵' }),
      scene(1600, { menu: true, pointer: [494, 306] }),
      scene(350, { menu: true, pointer: [494, 306], click: true }),
      scene(1500, { picker: true }), scene(2100, { picker: true, typed: 'ifb' }),
      scene(600, { picker: true, typed: 'ifb', pointer: [1026, 558] }),
      scene(300, { picker: true, typed: 'ifb', pointer: [1026, 558], click: true }),
      scene(1700, { applied: true }), scene(1900, { applied: true, edited: true }),
      scene(550, { applied: true, edited: true, key: 'Tab' }),
      scene(2200, { applied: true, edited: true, done: true })
    ]
  },
  'insert-template': {
    title: 'INSERT 欄位與預留值',
    caption: 'INSERT INTO 後輸入 libt → 選擇 Lib_Tag → Tab 展開 → 填入標籤名稱。自動略過 IDENTITY，依欄位產生 N\'\'、DEFAULT 或 NULL；不執行 INSERT。',
    draw: insertDemo, poster: 7,
    frames: [
      scene(1200, {}), scene(250, { typed: 'l' }), scene(250, { typed: 'li' }),
      scene(250, { typed: 'lib' }), scene(1300, { typed: 'libt' }),
      scene(650, { typed: 'libt', key: 'Tab' }),
      scene(900, { expanded: true, key: 'Tab' }), scene(3000, { expanded: true }),
      scene(2500, { expanded: true, filled: true })
    ]
  },
  'execute-template': {
    title: 'EXEC 具名參數與 OUTPUT',
    caption: '在 EXEC dbo. 後選擇 usp_Loan_Count → 按 Tab 產生具名參數、選擇性參數註解與 OUTPUT 變數宣告；不執行程序。',
    draw: executeDemo, poster: 5,
    frames: [
      scene(1200, {}), scene(350, { typed: 'usp_' }),
      scene(1400, { typed: 'usp_Loan' }), scene(650, { typed: 'usp_Loan', key: 'Tab' }),
      scene(1000, { expanded: true, key: 'Tab' }), scene(3700, { expanded: true })
    ]
  },
  'merge-template': {
    title: 'MERGE 安全骨架',
    caption: '在 MERGE INTO dbo. 後選擇 Cat_BookCopy → 按 Tab 產生主鍵比對、UPDATE 與 INSERT；使用前須替換 dbo.SourceTable，並檢查兩個 AND 1 = 0 安全條件。',
    draw: mergeDemo, poster: 5,
    frames: [
      scene(1200, {}), scene(350, { typed: 'Cat_' }),
      scene(1400, { typed: 'Cat_Book' }), scene(650, { typed: 'Cat_Book', key: 'Tab' }),
      scene(1000, { expanded: true, key: 'Tab' }), scene(4200, { expanded: true })
    ]
  },
  'alter-procedure': {
    title: 'ALTER PROCEDURE 完整定義',
    caption: '在 ALTER PROCEDURE dbo. 後選擇 usp_Loan_Count → 按 Tab 載入可編輯的完整程序定義；不執行 ALTER。',
    draw: alterProcedureDemo, poster: 5,
    frames: [
      scene(1200, {}), scene(350, { typed: 'usp_' }),
      scene(1400, { typed: 'usp_Loan' }), scene(650, { typed: 'usp_Loan', key: 'Tab' }),
      scene(1000, { expanded: true, key: 'Tab' }), scene(3700, { expanded: true })
    ]
  },
  'alter-function': {
    title: 'ALTER FUNCTION 完整定義',
    caption: '在 ALTER FUNCTION dbo. 後選擇 fn_LoanCount → 按 Tab 載入可編輯的完整函式定義；不執行 ALTER。',
    draw: alterFunctionDemo, poster: 5,
    frames: [
      scene(1200, {}), scene(350, { typed: 'fn_' }),
      scene(1400, { typed: 'fn_Loan' }), scene(650, { typed: 'fn_Loan', key: 'Tab' }),
      scene(1000, { expanded: true, key: 'Tab' }), scene(3700, { expanded: true })
    ]
  },
  'result-in': {
    title: '結果格線轉 IN 條件',
    caption: '選取 CopyNo 的前三格 → 右鍵「複製成 IN 條件」→ 在查詢 WHERE 後 Ctrl+V。相同值會去重，沒有選取的 B003 不會帶入。',
    draw: gridDemo, poster: 9,
    frames: [
      scene(1200, {}), scene(1600, { selected: true, pointer: [187, 445] }),
      scene(350, { selected: true, pointer: [187, 445], click: true, key: '右鍵' }),
      scene(1600, { selected: true, menu: true, pointer: [411, 349] }),
      scene(350, { selected: true, menu: true, pointer: [411, 349], click: true }),
      scene(1100, { selected: true, copied: true }), scene(1700, { pasteView: true }),
      scene(650, { pasteView: true, key: 'Ctrl + V' }),
      scene(900, { pasteView: true, pasted: true, key: 'Ctrl + V' }),
      scene(3200, { pasteView: true, pasted: true })
    ]
  }
});

window.afterDemoRender = () => {
  document.querySelectorAll('#stage [data-scroll]').forEach(node => {
    node.scrollTop = Number(node.dataset.scroll);
  });
};
