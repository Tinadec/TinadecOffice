import i18n from '@/i18n'

export type GitDiffLineChange = 'context' | 'add' | 'delete';

export interface GitDiffLine {
  id: string;
  change: GitDiffLineChange;
  old_line_number: number | null;
  new_line_number: number | null;
  content: string;
}

export interface GitDiffHunk {
  id: string;
  header: string;
  old_start: number;
  new_start: number;
  lines: GitDiffLine[];
}

export interface GitDiffFile {
  id: string;
  path: string;
  previous_path: string | null;
  change_type: string;
  binary: boolean;
  hunks: GitDiffHunk[];
  notice: string | null;
}

export interface GitParsedDiff {
  files: GitDiffFile[];
  notice: string | null;
}

const HUNK_HEADER = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/;

export function parseUnifiedDiff(diff: string): GitParsedDiff {
  const files: GitDiffFile[] = [];
  let currentFile: GitDiffFile | null = null;
  let currentHunk: GitDiffHunk | null = null;
  let oldLineNumber = 0;
  let newLineNumber = 0;

  for (const line of diff.split(/\r?\n/)) {
    if (line.startsWith('diff --git ')) {
      currentFile = createFileFromHeader(line, files.length);
      files.push(currentFile);
      currentHunk = null;
      continue;
    }

    if (!currentFile) {
      continue;
    }

    if (line.startsWith('rename from ')) {
      currentFile.previous_path = line.slice('rename from '.length);
      currentFile.change_type = 'renamed';
      continue;
    }

    if (line.startsWith('rename to ')) {
      currentFile.path = line.slice('rename to '.length);
      continue;
    }

    if (line.startsWith('new file mode ')) {
      currentFile.change_type = 'added';
      continue;
    }

    if (line.startsWith('deleted file mode ')) {
      currentFile.change_type = 'deleted';
      continue;
    }

    if (line.startsWith('Binary files ') || line.startsWith('Binary file ')) {
      currentFile.binary = true;
      currentFile.notice = line;
      continue;
    }

    if (line.startsWith('--- ')) {
      const previousPath = stripDiffPath(line.slice(4));
      if (previousPath && previousPath !== '/dev/null') {
        currentFile.previous_path = previousPath;
      }
      continue;
    }

    if (line.startsWith('+++ ')) {
      const nextPath = stripDiffPath(line.slice(4));
      if (nextPath && nextPath !== '/dev/null') {
        currentFile.path = nextPath;
      }
      continue;
    }

    if (line.startsWith('@@ ')) {
      const match = HUNK_HEADER.exec(line);
      oldLineNumber = Number(match?.[1] ?? 0);
      newLineNumber = Number(match?.[2] ?? 0);
      currentHunk = {
        id: `${currentFile.id}:hunk:${currentFile.hunks.length}`,
        header: line,
        old_start: oldLineNumber,
        new_start: newLineNumber,
        lines: []
      };
      currentFile.hunks.push(currentHunk);
      continue;
    }

    if (!currentHunk || line.length === 0) {
      continue;
    }

    const prefix = line[0];
    const content = line.slice(1);
    if (prefix === '+') {
      currentHunk.lines.push({
        id: `${currentHunk.id}:line:${currentHunk.lines.length}`,
        change: 'add',
        old_line_number: null,
        new_line_number: newLineNumber++,
        content
      });
      continue;
    }

    if (prefix === '-') {
      currentHunk.lines.push({
        id: `${currentHunk.id}:line:${currentHunk.lines.length}`,
        change: 'delete',
        old_line_number: oldLineNumber++,
        new_line_number: null,
        content
      });
      continue;
    }

    if (prefix === ' ') {
      currentHunk.lines.push({
        id: `${currentHunk.id}:line:${currentHunk.lines.length}`,
        change: 'context',
        old_line_number: oldLineNumber++,
        new_line_number: newLineNumber++,
        content
      });
    }
  }

  return {
    files,
    notice: diff.trim().length === 0 ? i18n.global.t('context.gitNoDiffText') : null
  };
}

function createFileFromHeader(line: string, index: number): GitDiffFile {
  const parts = line.split(' ');
  const previousPath = stripDiffPath(parts[2] ?? '') ?? null;
  const currentPath = stripDiffPath(parts[3] ?? '') ?? previousPath ?? `file-${index + 1}`;
  return {
    id: `file:${index}:${currentPath}`,
    path: currentPath,
    previous_path: previousPath && previousPath !== currentPath ? previousPath : null,
    change_type: 'modified',
    binary: false,
    hunks: [],
    notice: null
  };
}

function stripDiffPath(value: string): string | null {
  if (!value) return null;
  if (value === '/dev/null') return value;
  return value.replace(/^a\//, '').replace(/^b\//, '');
}
