import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

/**
 * Shell of the global-admin area. Visibility-only: the route sits behind adminGuard and every
 * endpoint the child pages will call is additionally gated server-side by
 * GlobalAdminAuthorizationFilter. Tab bar follows ChannelWorkspaceLayout's pattern.
 */
@Component({
  selector: 'app-admin-layout',
  imports: [RouterLink, RouterLinkActive, RouterOutlet, TranslocoPipe],
  template: `
    <div>
      <h1 class="mb-4 text-2xl font-bold tracking-tight">{{ 'admin.title' | transloco }}</h1>

      <nav class="mb-6 flex gap-2 border-b border-slate-800">
        <a
          routerLink="monitoring"
          routerLinkActive
          ariaCurrentWhenActive="page"
          #monitoringTab="routerLinkActive"
          [class]="
            monitoringTab.isActive
              ? 'border-b-2 border-purple-500 px-3 py-2 text-sm text-slate-100 transition'
              : 'border-b-2 border-transparent px-3 py-2 text-sm text-slate-400 transition hover:text-slate-200'
          "
        >
          {{ 'admin.tabs.monitoring' | transloco }}
        </a>
        <a
          routerLink="channels"
          routerLinkActive
          ariaCurrentWhenActive="page"
          #channelsTab="routerLinkActive"
          [class]="
            channelsTab.isActive
              ? 'border-b-2 border-purple-500 px-3 py-2 text-sm text-slate-100 transition'
              : 'border-b-2 border-transparent px-3 py-2 text-sm text-slate-400 transition hover:text-slate-200'
          "
        >
          {{ 'admin.tabs.channels' | transloco }}
        </a>
        <a
          routerLink="audit-log"
          routerLinkActive
          ariaCurrentWhenActive="page"
          #auditLogTab="routerLinkActive"
          [class]="
            auditLogTab.isActive
              ? 'border-b-2 border-purple-500 px-3 py-2 text-sm text-slate-100 transition'
              : 'border-b-2 border-transparent px-3 py-2 text-sm text-slate-400 transition hover:text-slate-200'
          "
        >
          {{ 'admin.tabs.auditLog' | transloco }}
        </a>
      </nav>

      <router-outlet />
    </div>
  `,
})
export class AdminLayout {}
