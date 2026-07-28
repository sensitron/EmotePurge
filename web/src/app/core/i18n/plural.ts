/** No ICU MessageFormat plugin installed — Transloco resolves plain keys, so plural selection is
 * done by picking between two sibling keys (`<baseKey>.one` / `<baseKey>.other`) ourselves. */
export function pluralKey(count: number, baseKey: string): string {
  return `${baseKey}.${count === 1 ? 'one' : 'other'}`;
}
