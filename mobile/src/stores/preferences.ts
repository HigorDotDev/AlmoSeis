/** Preferências locais do aparelho (08 §8.10 `menu/settings`): som, fonte da cozinha, última aba, tema. */
import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

import { preferencesStorage } from '@/db/kvStorage';

export type TabName = 'marmitas' | 'salao' | 'pedidos' | 'fechamento';
/** `system` segue o claro/escuro do aparelho (ou do navegador). */
export type ThemeMode = 'system' | 'light' | 'dark';

interface PreferencesState {
  orderSound: boolean;
  kitchenFontScale: number;
  lastTab: TabName | null;
  themeMode: ThemeMode;
  setOrderSound(on: boolean): void;
  setKitchenFontScale(scale: number): void;
  setLastTab(tab: TabName): void;
  setThemeMode(mode: ThemeMode): void;
}

export const usePreferences = create<PreferencesState>()(
  persist(
    (set) => ({
      orderSound: true,
      kitchenFontScale: 1.2,
      lastTab: null,
      themeMode: 'system',
      setOrderSound: (orderSound) => set({ orderSound }),
      setKitchenFontScale: (kitchenFontScale) => set({ kitchenFontScale }),
      setLastTab: (lastTab) => set({ lastTab }),
      setThemeMode: (themeMode) => set({ themeMode }),
    }),
    { name: 'almoseis.preferences', storage: createJSONStorage(() => preferencesStorage) },
  ),
);
