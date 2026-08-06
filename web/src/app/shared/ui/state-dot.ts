import { Component, computed, input } from '@angular/core';

export type StateDotTone = 'on' | 'off';

/**
 * The state a row is *in*, as opposed to a property it *has*.
 *
 * Three list pages had drifted into rendering both as the same pill. An overview row could carry
 * five of them at once — live, broadcaster, moderator, 7TV editor, bot active — which flattened
 * three unrelated kinds of information into one texture: who you are here (a fact about you, the
 * same on nearly every row), whether the channel is streaming right now, and whether anything is
 * being measured at all. Only the last one is the row's own condition, and it is the one that
 * decides whether the channel is producing data.
 *
 * A dot plus a word says that without competing with the badges around it. `-dot` tokens exist for
 * exactly this: a small meaning-bearing graphic, which owes 3:1 rather than 4,5:1 — the word next
 * to it carries the meaning on its own, so the colour never has to.
 */
const TONE_CLASSES: Record<StateDotTone, { dot: string; text: string }> = {
  on: { dot: 'bg-success-dot', text: 'text-fg-secondary' },
  off: { dot: 'bg-border-strong', text: 'text-fg-muted' },
};

@Component({
  selector: 'app-state-dot',
  template: `<span [class]="'inline-flex items-center gap-1.5 text-xs whitespace-nowrap ' + text()">
    <span [class]="'h-1.5 w-1.5 shrink-0 rounded-full ' + dot()" aria-hidden="true"></span>
    <ng-content />
  </span>`,
})
export class StateDot {
  readonly tone = input.required<StateDotTone>();

  protected readonly dot = computed(() => TONE_CLASSES[this.tone()].dot);
  protected readonly text = computed(() => TONE_CLASSES[this.tone()].text);
}
