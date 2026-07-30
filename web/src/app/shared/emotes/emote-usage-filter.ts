import { computed, signal } from '@angular/core';

interface FilterableEmote {
  emoteName: string;
  totalUseCount: number;
}

function globToRegExp(pattern: string): RegExp {
  const trimmed = pattern.trim();
  const escaped = trimmed
    .replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*/g, '.*')
    .replace(/\?/g, '.');

  // With ~1,000 emotes per channel, the expected default is a plain substring search ("peepo"
  // should match "peepoHappy", "peepoSad", ...) — fully anchoring every query made that
  // impossible unless the user also typed wildcards. Only anchor (classic glob semantics) once
  // the user opts in by actually typing `*`/`?`; a bare query stays an unanchored substring match.
  const hasWildcard = trimmed.includes('*') || trimmed.includes('?');
  return hasWildcard ? new RegExp(`^${escaped}$`, 'i') : new RegExp(escaped, 'i');
}

/**
 * Min/Max-usage + name-wildcard filtering, used identically on the Usage-Stats and
 * Voting-Detail emote grids. Not a service — page-local UI state constructed per host
 * component, like ListSelection.
 */
export class EmoteUsageFilter<T extends FilterableEmote> {
  private readonly minCount = signal<number | null>(null);
  private readonly maxCount = signal<number | null>(null);
  private readonly nameFilter = signal('');

  readonly min = this.minCount.asReadonly();
  readonly max = this.maxCount.asReadonly();
  readonly nameQuery = this.nameFilter.asReadonly();
  readonly isUnusedActive = computed(() => this.minCount() === 0 && this.maxCount() === 0);

  private readonly nameFilterRegex = computed(() => {
    const query = this.nameFilter();
    return query.trim() === '' ? null : globToRegExp(query);
  });

  /** Called after every filter change — hosts typically use this to clear stale selections. */
  constructor(private readonly onChange: () => void = () => {}) {}

  apply(items: readonly T[]): T[] {
    const min = this.minCount();
    const max = this.maxCount();
    const nameRegex = this.nameFilterRegex();
    return items.filter((item) => {
      if (min !== null && item.totalUseCount < min) return false;
      if (max !== null && item.totalUseCount > max) return false;
      if (nameRegex && !nameRegex.test(item.emoteName)) return false;
      return true;
    });
  }

  setMinCount(value: string): void {
    const parsed = value.trim() === '' ? null : Number(value);
    this.minCount.set(parsed === null || Number.isNaN(parsed) ? null : parsed);
    this.onChange();
  }

  setMaxCount(value: string): void {
    const parsed = value.trim() === '' ? null : Number(value);
    this.maxCount.set(parsed === null || Number.isNaN(parsed) ? null : parsed);
    this.onChange();
  }

  setNameFilter(value: string): void {
    this.nameFilter.set(value);
    this.onChange();
  }

  toggleUnused(): void {
    if (this.isUnusedActive()) {
      this.minCount.set(null);
      this.maxCount.set(null);
    } else {
      this.minCount.set(0);
      this.maxCount.set(0);
    }
    this.onChange();
  }

  /** True whenever any filter would exclude items — the empty state uses this to offer a reset. */
  isAnyActive(): boolean {
    return this.minCount() !== null || this.maxCount() !== null || this.nameFilter().trim() !== '';
  }

  reset(): void {
    this.minCount.set(null);
    this.maxCount.set(null);
    this.nameFilter.set('');
    this.onChange();
  }
}
