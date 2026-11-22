/** Preferências locais do aparelho (08 §8.10 `menu/settings`): som, fonte da cozinha, última aba, tema e o aviso dos pedidos do site com o app fechado (ADR-0049 item 6). */
import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

import { preferencesStorage } from '@/db/kvStorage';

export type TabName = 'marmitas' | 'salao' | 'delivery' | 'pedidos' | 'fechamento';
/** `system` segue o claro/escuro do aparelho (ou do navegador). */
export type ThemeMode = 'system' | 'light' | 'dark';

interface PreferencesState {
  orderSound: boolean;
  kitchenFontScale: number;
  lastTab: TabName | null;
  themeMode: ThemeMode;
  /**
   * "Avisar pedidos do site com o app fechado" deste aparelho (`webOrdersEnabled` do `PUT /devices/push-token`).
   * `null` = nunca mexeu: vai `null` e o servidor mantém a dele (ligada no primeiro registro).
   */
  webOrdersPush: boolean | null;
  /** "Agora não" na explicação antes do pedido de permissão; a opção continua em Configurações. */
  pushPromptDismissed: boolean;
  setOrderSound(on: boolean): void;
  setKitchenFontScale(scale: number): void;
  setLastTab(tab: TabName): void;
  setThemeMode(mode: ThemeMode): void;
  setWebOrdersPush(on: boolean): void;
  dismissPushPrompt(): void;
}

export const usePreferences = create<PreferencesState>()(
  persist(
    (set) => ({
      orderSound: true,
      kitchenFontScale: 1.2,
      lastTab: null,
      themeMode: 'system',
      webOrdersPush: null,
      pushPromptDismissed: false,
      setOrderSound: (orderSound) => set({ orderSound }),
      setKitchenFontScale: (kitchenFontScale) => set({ kitchenFontScale }),
      setLastTab: (lastTab) => set({ lastTab }),
      setThemeMode: (themeMode) => set({ themeMode }),
      setWebOrdersPush: (webOrdersPush) => set({ webOrdersPush }),
      dismissPushPrompt: () => set({ pushPromptDismissed: true }),
    }),
    { name: 'almoseis.preferences', storage: createJSONStorage(() => preferencesStorage) },
  ),
);
