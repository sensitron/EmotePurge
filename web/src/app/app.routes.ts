import { Routes } from '@angular/router';

import { authGuard } from './core/auth/auth.guard';
import { DashboardPage } from './features/dashboard/dashboard-page';
import { LoginPage } from './features/login/login-page';

export const routes: Routes = [
  { path: 'login', component: LoginPage },
  { path: '', component: DashboardPage, canActivate: [authGuard] },
  { path: '**', redirectTo: '' },
];
