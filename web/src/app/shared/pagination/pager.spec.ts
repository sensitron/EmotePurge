import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { Pager } from './pager';

// Only the keys this primitive translates — not the full app translation file.
const DE_TRANSLATIONS = {
  pager: {
    label: 'Seitennummerierung',
    previous: 'Zurück',
    pageOf: 'Seite {{ page }} von {{ totalPages }}',
    next: 'Weiter',
  },
};

@Component({
  imports: [Pager],
  template: `
    <h2 #anchor tabindex="-1">Ergebnisse</h2>
    <app-pager
      [page]="page()"
      [totalPages]="totalPages()"
      [scrollTarget]="withTarget() ? anchor : undefined"
      (pageChange)="onPageChange($event)"
    />
  `,
})
class Host {
  readonly page = signal(2);
  readonly totalPages = signal(5);
  readonly withTarget = signal(true);

  /** Both jobs of a click land here in the order they happened — see the ordering spec. */
  readonly log: string[] = [];

  onPageChange(page: number): void {
    this.log.push(`emit:${page}`);
  }
}

describe('Pager', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        Host,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  /**
   * jsdom implements neither `scrollIntoView` nor any layout behind it, so the anchor gets a stand-in
   * that records the call into the host's log — which is what makes the call *order* observable.
   */
  function render(): {
    fixture: ComponentFixture<Host>;
    host: Host;
    anchor: HTMLHeadingElement;
    buttons: HTMLButtonElement[];
    status: HTMLElement | null;
  } {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const host = fixture.componentInstance;
    const anchor: HTMLHeadingElement = fixture.nativeElement.querySelector('h2');
    anchor.scrollIntoView = () => host.log.push('scroll');
    return {
      fixture,
      host,
      anchor,
      buttons: Array.from(fixture.nativeElement.querySelectorAll('button')),
      status: fixture.nativeElement.querySelector('[role="status"]'),
    };
  }

  it('hides itself at a single page — a pager over one page is pure noise', () => {
    const { fixture, host } = render();

    host.totalPages.set(1);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('nav')).toBeNull();
  });

  it('emits the neighbouring page numbers', () => {
    const { host, buttons } = render();
    const [previous, next] = buttons;

    next.click();
    previous.click();

    expect(host.log.filter((entry) => entry.startsWith('emit:'))).toEqual(['emit:3', 'emit:1']);
  });

  it('disables the edge it is standing on', () => {
    const { fixture, host, buttons } = render();
    const [previous, next] = buttons;

    host.page.set(1);
    fixture.detectChanges();
    expect(previous.disabled).toBe(true);
    expect(next.disabled).toBe(false);

    host.page.set(5);
    fixture.detectChanges();
    expect(previous.disabled).toBe(false);
    expect(next.disabled).toBe(true);
  });

  // The point of the whole feature: the pager sits at the bottom of a long list, so without this the
  // next page's rows arrive above the viewport and the reader keeps staring at the end of the old one.
  it('scrolls and focuses the target before emitting — after the emit the list is already gone', () => {
    const { host, anchor, buttons } = render();
    const [, next] = buttons;

    next.click();

    expect(host.log).toEqual(['scroll', 'emit:3']);
    expect(document.activeElement).toBe(anchor);
  });

  it('still emits when no scroll target was given', () => {
    const { fixture, host, buttons } = render();
    const [, next] = buttons;

    host.withTarget.set(false);
    fixture.detectChanges();
    next.click();

    expect(host.log).toEqual(['emit:3']);
  });

  it('is a named landmark and announces the page it switched to', () => {
    const { fixture, status } = render();

    expect(fixture.nativeElement.querySelector('nav')?.getAttribute('aria-label')).toBe(
      'Seitennummerierung',
    );
    // role="status" is an implicit polite live region — moving focus announces *what* is on screen,
    // never which page of it.
    expect(status?.textContent?.trim()).toBe('Seite 2 von 5');
  });
});
