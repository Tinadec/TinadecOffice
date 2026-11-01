/**
 * Agent browser shell labels.
 *
 * The agent browser is a separate window with a tiny plain-HTML toolbar, so it cannot use the
 * renderer's vue-i18n bundles. Like `TRAY_STRINGS`, this table is the main-process copy of the
 * five interface languages; keep it in sync when adding a label. `resolveLocale` deliberately
 * mirrors `resolveTrayLocale` (tray.cjs) instead of importing it, so a Node test of the shell
 * strings does not pull the tray module and its Electron imports.
 */

const DEFAULT_LOCALE = 'zh-CN';

const AGENT_BROWSER_STRINGS = {
  'zh-CN': {
    title: '智能体浏览器',
    back: '后退',
    forward: '前进',
    reload: '刷新',
    urlPlaceholder: '输入网址，按 Enter 打开',
    tabs: '标签页',
    agentControl: '智能体正在操作',
    userControl: '你已接管浏览器',
    takeOver: '接管',
    handBack: '交还给智能体',
    openExternal: '在系统浏览器打开',
    close: '关闭',
    loading: '正在加载…',
    dialogOpen: '页面弹出了对话框，智能体已暂停',
    noticePopup: '已阻止弹出窗口',
    noticePermission: '已拒绝页面权限请求：{permission}',
    noticeDownload: '已阻止下载：{filename}',
    noticeFileChooser: '页面请求选择文件，智能体已暂停',
    noticeRenderer: '页面渲染进程已退出',
    noticeNavigation: '已阻止不支持的地址',
  },
  'zh-TW': {
    title: '智慧體瀏覽器',
    back: '上一頁',
    forward: '下一頁',
    reload: '重新載入',
    urlPlaceholder: '輸入網址，按 Enter 開啟',
    tabs: '分頁',
    agentControl: '智慧體正在操作',
    userControl: '你已接管瀏覽器',
    takeOver: '接管',
    handBack: '交還給智慧體',
    openExternal: '在系統瀏覽器開啟',
    close: '關閉',
    loading: '正在載入…',
    dialogOpen: '頁面彈出對話框，智慧體已暫停',
    noticePopup: '已阻擋彈出視窗',
    noticePermission: '已拒絕頁面權限請求：{permission}',
    noticeDownload: '已阻擋下載：{filename}',
    noticeFileChooser: '頁面要求選擇檔案，智慧體已暫停',
    noticeRenderer: '頁面轉譯程序已結束',
    noticeNavigation: '已阻擋不支援的位址',
  },
  en: {
    title: 'Agent Browser',
    back: 'Back',
    forward: 'Forward',
    reload: 'Reload',
    urlPlaceholder: 'Enter an address and press Enter',
    tabs: 'Tabs',
    agentControl: 'Agent is browsing',
    userControl: 'You have control',
    takeOver: 'Take over',
    handBack: 'Hand back to agent',
    openExternal: 'Open in system browser',
    close: 'Close',
    loading: 'Loading…',
    dialogOpen: 'The page opened a dialog; the agent is paused',
    noticePopup: 'Blocked a popup',
    noticePermission: 'Denied a permission request: {permission}',
    noticeDownload: 'Blocked a download: {filename}',
    noticeFileChooser: 'The page asked for a file; the agent is paused',
    noticeRenderer: 'The page renderer exited',
    noticeNavigation: 'Blocked an unsupported address',
  },
  ja: {
    title: 'エージェントブラウザ',
    back: '戻る',
    forward: '進む',
    reload: '再読み込み',
    urlPlaceholder: 'アドレスを入力して Enter',
    tabs: 'タブ',
    agentControl: 'エージェントが操作中',
    userControl: 'あなたが操作しています',
    takeOver: '引き継ぐ',
    handBack: 'エージェントに戻す',
    openExternal: 'システムブラウザで開く',
    close: '閉じる',
    loading: '読み込み中…',
    dialogOpen: 'ページがダイアログを開いたため、エージェントは停止しています',
    noticePopup: 'ポップアップをブロックしました',
    noticePermission: '権限リクエストを拒否しました: {permission}',
    noticeDownload: 'ダウンロードをブロックしました: {filename}',
    noticeFileChooser: 'ファイル選択が要求されたため、エージェントは停止しています',
    noticeRenderer: 'ページのレンダラーが終了しました',
    noticeNavigation: '未対応のアドレスをブロックしました',
  },
  ru: {
    title: 'Браузер агента',
    back: 'Назад',
    forward: 'Вперёд',
    reload: 'Обновить',
    urlPlaceholder: 'Введите адрес и нажмите Enter',
    tabs: 'Вкладки',
    agentControl: 'Агент работает в браузере',
    userControl: 'Управление у вас',
    takeOver: 'Перехватить',
    handBack: 'Вернуть агенту',
    openExternal: 'Открыть в системном браузере',
    close: 'Закрыть',
    loading: 'Загрузка…',
    dialogOpen: 'Страница открыла диалог; агент приостановлен',
    noticePopup: 'Всплывающее окно заблокировано',
    noticePermission: 'Запрос разрешения отклонён: {permission}',
    noticeDownload: 'Загрузка заблокирована: {filename}',
    noticeFileChooser: 'Страница запросила файл; агент приостановлен',
    noticeRenderer: 'Процесс отрисовки страницы завершился',
    noticeNavigation: 'Неподдерживаемый адрес заблокирован',
  },
};

/** Map any locale tag (OS locale, renderer value) onto one of the five bundled languages. */
function resolveLocale(raw) {
  const value = typeof raw === 'string' ? raw.trim() : '';
  if (Object.hasOwn(AGENT_BROWSER_STRINGS, value)) return value;
  const tag = value.toLowerCase();
  if (!tag) return DEFAULT_LOCALE;
  if (tag.startsWith('zh-tw') || tag.startsWith('zh-hk') || tag.startsWith('zh-mo') || tag.includes('hant')) return 'zh-TW';
  if (tag.startsWith('zh')) return 'zh-CN';
  if (tag.startsWith('en')) return 'en';
  if (tag.startsWith('ja')) return 'ja';
  if (tag.startsWith('ru')) return 'ru';
  return DEFAULT_LOCALE;
}

function stringsForLocale(locale) {
  return AGENT_BROWSER_STRINGS[resolveLocale(locale)];
}

module.exports = { AGENT_BROWSER_STRINGS, DEFAULT_LOCALE, resolveLocale, stringsForLocale };
