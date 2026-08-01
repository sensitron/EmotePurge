import { Component, computed, input } from '@angular/core';

import { TranslocoPipe } from '@jsverse/transloco';

import { voteAudience } from '../../core/voting/vote-audience';
import { StatusBadge, StatusBadgeTone } from '../ui/status-badge';

/**
 * Names the audience a vote session was created for. Both voting pages carry it: the roles are
 * fixed at creation and cannot be changed afterwards, so without the badge the only place they were
 * ever visible was the create form.
 *
 * Tone follows the badge table in the design doc as far as it reaches: an open vote is the
 * unremarkable case and stays `slate` (neutral), a restricted one gets `blue` — the same "notable
 * property of this session" reading the "Verdeckt" badge next to it already uses.
 */
@Component({
  selector: 'app-vote-audience-badge',
  imports: [StatusBadge, TranslocoPipe],
  template: `<app-status-badge [tone]="tone()">
    {{ 'voting.audience.' + audience() | transloco }}
  </app-status-badge>`,
})
export class VoteAudienceBadge {
  readonly roles = input.required<number>();

  protected readonly audience = computed(() => voteAudience(this.roles()));
  protected readonly tone = computed<StatusBadgeTone>(() =>
    this.audience() === 'everyone' ? 'slate' : 'blue',
  );
}
