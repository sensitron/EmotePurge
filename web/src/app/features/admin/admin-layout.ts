import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { BackLink } from '../../shared/ui/back-link';

/**
 * Shell of the global-admin area. Visibility-only: the route sits behind adminGuard and every
 * endpoint the child pages will call is additionally gated server-side by
 * GlobalAdminAuthorizationFilter. Tab bar follows ChannelWorkspaceLayout's pattern.
 */
@Component({
  selector: 'app-admin-layout',
  imports: [BackLink, RouterLink, RouterLinkActive, RouterOutlet, TranslocoPipe],
  template: `
    <div>
      <!-- The tab bar below only navigates WITHIN /admin — without this the area was a dead end
           (design doc §8.6). Same header row shape as ChannelWorkspaceLayout. -->
      <div class="mb-4 flex flex-wrap items-center gap-x-4 gap-y-2">
        <app-back-link link="/" [label]="'nav.overview' | transloco" />
        <h1 class="text-2xl font-bold tracking-tight">{{ 'admin.title' | transloco }}</h1>
      </div>

      <!-- Sticky under the h-14 shell header; h-10 is a contract — filter toolbars pin at
           top-24 (= 14 + 10). Links are flex/items-center so the fixed height carries exactly. -->
      <nav class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 border-b border-slate-800">
        <a
          routerLink="monitoring"
          routerLinkActive
          ariaCurrentWhenActive="page"
          #monitoringTab="routerLinkActive"
          [class]="
            monitoringTab.isActive
              ? 'flex items-center border-b-2 border-purple-500 px-3 text-sm text-slate-100 transition'
              : 'flex items-center border-b-2 border-transparent px-3 text-sm text-slate-400 transition hover:text-slate-200'
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
              ? 'flex items-center border-b-2 border-purple-500 px-3 text-sm text-slate-100 transition'
              : 'flex items-center border-b-2 border-transparent px-3 text-sm text-slate-400 transition hover:text-slate-200'
          "
        >
          {{ 'admin.tabs.channels' | transloco }}
        </a>
        <a
          routerLink="users"
          routerLinkActive
          ariaCurrentWhenActive="page"
          #usersTab="routerLinkActive"
          [class]="
            usersTab.isActive
              ? 'flex items-center border-b-2 border-purple-500 px-3 text-sm text-slate-100 transition'
              : 'flex items-center border-b-2 border-transparent px-3 text-sm text-slate-400 transition hover:text-slate-200'
          "
        >
          {{ 'admin.tabs.users' | transloco }}
        </a>
        <a
          routerLink="audit-log"
          routerLinkActive
          ariaCurrentWhenActive="page"
          #auditLogTab="routerLinkActive"
          [class]="
            auditLogTab.isActive
              ? 'flex items-center border-b-2 border-purple-500 px-3 text-sm text-slate-100 transition'
              : 'flex items-center border-b-2 border-transparent px-3 text-sm text-slate-400 transition hover:text-slate-200'
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
