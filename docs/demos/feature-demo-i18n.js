// 頁面文案與操作示意分開翻譯；SQL 識別字保留原樣。
const demoI18n = {
  zh: {
    heading: '核心功能操作示意', features: '項功能', choose: '選擇功能',
    light: '淺色', dark: '深色', play: '播放', pause: '暫停', restart: '從頭播放',
    still: '查看靜態圖', theme: '主題', display: '顯示設定', viewer: 'SqlAssist 操作示意',
    disclaimer: '依新版 SSMS 22 畫面重建的示意動畫，並非實機錄影。所有內容皆為虛構圖書館資料；操作節奏經過縮短，不代表效能。'
  },
  en: {
    heading: 'Feature demos', features: 'features', choose: 'Choose a feature',
    light: 'Light', dark: 'Dark', play: 'Play', pause: 'Pause', restart: 'Replay',
    still: 'View still image', theme: 'Theme', display: 'Display settings', viewer: 'SqlAssist feature demo',
    disclaimer: 'Animated recreations of SSMS 22, not recordings. All library data is fictional. Timing is shortened and does not represent performance.'
  }
};

const demoEnglish = {
  'completion-preview': ['Completion and structure preview', 'Type libr → select Lib_Reader → press → to preview columns → press Tab to insert the name.'],
  'structure-preview': ['Object structure in completion', 'Type libr → select Lib_Reader → press → to open the structure preview → choose Script to inspect the table DDL without leaving the query window.'],
  'f12-definition': ['Open table DDL with F12', 'Place the caret on Loan → press F12 → inspect columns, keys, indexes, foreign keys and descriptions in a new query. The DDL is not executed.'],
  'expand-star': ['Expand SELECT * with Tab', 'Place the caret after * → press Tab when prompted to expand explicit columns. This example uses one column per line.'],
  'insert-template': ['INSERT columns and values', 'Type libt after INSERT INTO → select Lib_Tag → press Tab → enter the tag name. IDENTITY is skipped and values are generated without executing INSERT.'],
  'execute-template': ['EXEC parameters and OUTPUT', 'Select usp_Loan_Count after EXEC dbo. → press Tab to generate named parameters, optional parameter comments and an OUTPUT variable. The procedure is not executed.'],
  'merge-template': ['MERGE template', 'Select Cat_BookCopy after MERGE INTO dbo. → press Tab to generate key matching, UPDATE and INSERT. Replace dbo.SourceTable and review both AND 1 = 0 safeguards before use.'],
  'alter-procedure': ['Full ALTER PROCEDURE definition', 'Select usp_Loan_Count after ALTER PROCEDURE dbo. → press Tab to load its editable definition. ALTER is not executed.'],
  'alter-function': ['Full ALTER FUNCTION definition', 'Select fn_LoanCount after ALTER FUNCTION dbo. → press Tab to load its editable definition. ALTER is not executed.'],
  'surround-snippet': ['Surround SQL with a snippet', 'Select SQL → right-click Surround with Snippet → search for ifb → review the preview → apply → enter the IF condition → press Tab to finish.'],
  'sql-search': ['Search SQL objects', 'Open Search → enter CopyNo → preview the table definition → move to the next match. Search covers object names, definitions and columns.'],
  'sql-memory': ['History and Favorites', 'Open History → search for Loan → save it as a favorite → open Loan list from Favorites in a new query. Opening it does not execute SQL.'],
  'result-in': ['Copy result cells as IN', 'Select the first three CopyNo cells → right-click Copy as IN condition → paste after WHERE. Duplicate values are removed; unselected B003 is excluded.']
};

const stageEnglish = {
  '檔案': 'File', '編輯': 'Edit', '檢視': 'View', '查詢': 'Query', '工具': 'Tools',
  '新增查詢': 'New Query', '執行': 'Execute', '已連線': 'Connected', '查詢已成功執行': 'Query executed successfully',
  '已複製 IN 條件': 'IN condition copied', '操作示意': 'Demo', '虛構資料': 'Fictional data',
  '複製選取': 'Copy selection', '複製全部': 'Copy all', '個欄位': 'columns', '個索引': 'index',
  '圖書館讀者基本資料': 'Library reader details', '欄位': 'Columns', '索引': 'Indexes', '指令碼': 'Script',
  '型別': 'Type', '旗標': 'Flags', '說明': 'Description', '讀者編號': 'Reader ID', '讀者姓名': 'Reader name',
  '借閱證號': 'Library card', '所屬分館': 'Branch', '建檔時間': 'Created at',
  '按 Tab 展開所有欄位': 'Press Tab to expand all columns', '伺服器': 'Server', '資料庫': 'Database',
  '查詢視窗': 'Query window', '種類': 'Kind', '全部': 'All', '名稱': 'Name', '內容': 'Content',
  '預覽': 'Preview', '已顯示全部': 'Showing all', '項': 'items', '筆': 'entries',
  '輸入關鍵字，搜尋物件名稱、內容與欄位': 'Enter a keyword to search object names, definitions and columns',
  '借閱清單': 'Loan list', '收藏': 'Favorite', '草稿': 'Draft', '今天': 'Today', '天': 'days',
  '設定': 'Settings', '已新增至收藏': 'Added to Favorites', '新增至收藏': 'Add to Favorites',
  '借閱館藏與讀者編號': 'Borrowed items and reader IDs', '儲存': 'Save', '取消': 'Cancel',
  '條件成立時執行區塊': 'Execute block when condition is true',
  '右鍵': 'Right-click', '建立預存程序': 'Create stored procedure', 'BEGIN／END 區塊': 'BEGIN/END block',
  '資料存在時執行': 'Execute when data exists', '資料不存在時執行': 'Execute when data does not exist',
  'WHILE 迴圈': 'WHILE loop', 'TRY／CATCH 例外處理': 'TRY/CATCH exception handling',
  '完整的本機快速順向 CURSOR 樣板': 'Local fast-forward CURSOR template',
  '安全交易樣板': 'Safe transaction template',
  '含 XACT_ABORT、TRY／CATCH 與回復的交易': 'Transaction with XACT_ABORT, TRY/CATCH and rollback',
  '交易試跑': 'Transaction dry run',
  '在交易裡試跑後回復；確認無誤再改成 COMMIT': 'Run in a transaction and roll back; change to COMMIT after review',
  '包成 CTE': 'Wrap in CTE', '把查詢放進 CTE 再查詢': 'Place the query in a CTE and query it',
  '包成衍生資料表': 'Wrap as a derived table', '把查詢放進 FROM 的子查詢': 'Place the query in a FROM subquery',
  '剪下': 'Cut', '複製': 'Copy', '貼上': 'Paste', '以片段包住選取範圍': 'Surround selection with snippet',
  '程式碼片段': 'Code snippets', '包住': 'Surround', '行': 'lines', '個字元，不會執行 SQL': 'characters; SQL will not run',
  '個可包夾片段': 'surround snippets', '套用後以 Tab 填寫 1 個欄位': 'Fill one field with Tab after applying',
  '選擇': 'Select', '套用': 'Apply', '結束欄位導航': 'Finish field navigation',
  '結果': 'Results', '訊息': 'Messages', '另存結果為': 'Save Results As',
  '複製成 IN 條件': 'Copy as IN condition', '複製成 Markdown 表格': 'Copy as Markdown table',
  '複製成 JSON': 'Copy as JSON', '建立 #temp 指令碼': 'Create #temp script',
  '欄位剖析': 'Column analysis', '檢視這一格完整內容': 'View full cell content'
};

const stageTerms = Object.entries(stageEnglish).sort((a, b) => b[0].length - a[0].length);
function translateStage(stage) {
  const walker = document.createTreeWalker(stage, NodeFilter.SHOW_TEXT);
  while (walker.nextNode()) {
    const node = walker.currentNode;
    if (node.parentElement.closest('.line, .preview-code, .surround-preview')) continue;
    let value = node.nodeValue;
    for (const [source, target] of stageTerms) value = value.replaceAll(source, target);
    if (value !== node.nodeValue) node.nodeValue = value;
  }
}
