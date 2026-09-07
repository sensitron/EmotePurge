import { Dialog } from '@angular/cdk/dialog';
import { ComponentType } from '@angular/cdk/portal';
import { describe, expect, it, vi } from 'vitest';

import { DIALOG_TITLE_ID, openAppDialog } from './dialog';

// openAppDialog never inspects the component, only forwards it to Dialog.open, so any class works.
class FakeDialogComponent {}
const component = FakeDialogComponent as ComponentType<unknown>;

// Pure function, no TestBed needed: `dialog.open` is a bare vi.fn() standing in for CDK's Dialog.
describe('openAppDialog', () => {
  it('sets ariaLabel and omits ariaLabelledBy when a label is given', () => {
    const dialogOpen = vi.fn();
    const dialog = { open: dialogOpen } as unknown as Dialog;

    openAppDialog(dialog, component, { ariaLabel: 'Channel verlassen' });

    const config = dialogOpen.mock.calls[0][1];
    expect(config.ariaLabel).toBe('Channel verlassen');
    expect(config).not.toHaveProperty('ariaLabelledBy');
  });

  it('sets ariaLabelledBy to the shared title id and omits ariaLabel when no label is given', () => {
    const dialogOpen = vi.fn();
    const dialog = { open: dialogOpen } as unknown as Dialog;

    openAppDialog(dialog, component, {});

    const config = dialogOpen.mock.calls[0][1];
    expect(config.ariaLabelledBy).toBe(DIALOG_TITLE_ID);
    expect(config).not.toHaveProperty('ariaLabel');
  });

  // The chrome classes this also sets are deliberately not asserted: renaming them changes
  // nothing a user experiences, which is rule 12's line between behaviour and template.
  it('forwards the component and the caller data unchanged', () => {
    const dialogOpen = vi.fn();
    const dialog = { open: dialogOpen } as unknown as Dialog;
    const data = { channelId: 'abc' };

    openAppDialog(dialog, component, { data });

    expect(dialogOpen.mock.calls[0][0]).toBe(component);
    expect(dialogOpen.mock.calls[0][1].data).toBe(data);
  });
});
