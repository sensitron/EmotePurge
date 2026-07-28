import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { LanguageSwitcher } from '../../shared/i18n/language-switcher';

interface Feature {
  icon: string;
  titleKey: string;
  descriptionKey: string;
  badgeKey?: string;
}

interface Step {
  titleKey: string;
  descriptionKey: string;
}

const FEATURES: Feature[] = [
  {
    icon: '💬',
    titleKey: 'landing.features.chatAnalytics.title',
    descriptionKey: 'landing.features.chatAnalytics.description',
  },
  {
    icon: '🗳️',
    titleKey: 'landing.features.communityVoting.title',
    badgeKey: 'landing.features.communityVoting.badge',
    descriptionKey: 'landing.features.communityVoting.description',
  },
  {
    icon: '🧹',
    titleKey: 'landing.features.massDelete.title',
    descriptionKey: 'landing.features.massDelete.description',
  },
  {
    icon: '🔐',
    titleKey: 'landing.features.twitchLogin.title',
    descriptionKey: 'landing.features.twitchLogin.description',
  },
];

const STEPS: Step[] = [
  { titleKey: 'landing.how.step1.title', descriptionKey: 'landing.how.step1.description' },
  { titleKey: 'landing.how.step2.title', descriptionKey: 'landing.how.step2.description' },
  { titleKey: 'landing.how.step3.title', descriptionKey: 'landing.how.step3.description' },
  { titleKey: 'landing.how.step4.title', descriptionKey: 'landing.how.step4.description' },
];

@Component({
  selector: 'app-landing-page',
  imports: [TranslocoPipe, LanguageSwitcher],
  templateUrl: './landing-page.html',
})
export class LandingPage {
  private readonly authService = inject(AuthService);

  protected readonly features = FEATURES;
  protected readonly steps = STEPS;

  protected login(): void {
    this.authService.login();
  }
}
