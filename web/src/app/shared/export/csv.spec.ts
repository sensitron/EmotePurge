import { describe, expect, it } from 'vitest';

import { CsvColumn, toCsv } from './csv';

interface Row {
  name: string | null;
  count: number | null;
}

const NAME_COLUMN: CsvColumn<Row> = { header: 'name', value: (row) => row.name };
const COUNT_COLUMN: CsvColumn<Row> = { header: 'count', value: (row) => row.count };

/** The writer prepends a UTF-8 BOM; the assertions below strip it to stay readable. */
function body(csv: string): string {
  return csv.replace(/^﻿/, '');
}

describe('toCsv', () => {
  it('starts with a UTF-8 BOM so Excel decodes non-ASCII names', () => {
    const csv = toCsv<Row>([], [NAME_COLUMN]);
    expect(csv.charCodeAt(0)).toBe(0xfeff);
  });

  it('uses CRLF line endings and a trailing newline', () => {
    const csv = body(toCsv<Row>([{ name: 'PogU', count: 1 }], [NAME_COLUMN, COUNT_COLUMN]));
    expect(csv).toBe('name,count\r\nPogU,1\r\n');
  });

  it('quotes values containing commas, quotes and newlines', () => {
    const rows: Row[] = [
      { name: 'a,b', count: 1 },
      { name: 'say "hi"', count: 2 },
      { name: 'two\nlines', count: 3 },
    ];
    const csv = body(toCsv(rows, [NAME_COLUMN]));
    expect(csv).toBe('name\r\n"a,b"\r\n"say ""hi"""\r\n"two\nlines"\r\n');
  });

  it('quotes values with leading or trailing whitespace', () => {
    const csv = body(toCsv<Row>([{ name: ' padded ', count: 1 }], [NAME_COLUMN]));
    expect(csv).toBe('name\r\n" padded "\r\n');
  });

  it('renders null as an empty cell', () => {
    const csv = body(toCsv<Row>([{ name: null, count: null }], [NAME_COLUMN, COUNT_COLUMN]));
    expect(csv).toBe('name,count\r\n,\r\n');
  });

  it('drops a column entirely when present() returns false', () => {
    const columns: CsvColumn<Row>[] = [
      NAME_COLUMN,
      { ...COUNT_COLUMN, present: (rows) => rows.some((row) => row.count !== null) },
    ];
    const csv = body(toCsv<Row>([{ name: 'PogU', count: null }], columns));
    expect(csv).toBe('name\r\nPogU\r\n');
  });

  it('prefixes formula triggers in string values with an apostrophe', () => {
    const rows: Row[] = [
      { name: '=SUM(A1)', count: 1 },
      { name: '+1', count: 2 },
      { name: '-1', count: 3 },
      { name: '@x', count: 4 },
      { name: '\tindent', count: 5 },
    ];
    const csv = body(toCsv(rows, [NAME_COLUMN]));
    // After the apostrophe prefix the tab is no longer leading, so no quoting is needed.
    expect(csv).toBe(`name\r\n'=SUM(A1)\r\n'+1\r\n'-1\r\n'@x\r\n'\tindent\r\n`);
  });

  it('leaves negative numbers alone — they must stay summable', () => {
    const csv = body(toCsv<Row>([{ name: 'x', count: -3 }], [COUNT_COLUMN]));
    expect(csv).toBe('count\r\n-3\r\n');
  });

  it('never locale-formats numbers', () => {
    const csv = body(toCsv<Row>([{ name: 'x', count: 1234567 }], [COUNT_COLUMN]));
    expect(csv).toBe('count\r\n1234567\r\n');
  });
});
