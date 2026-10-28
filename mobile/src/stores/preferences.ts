/** Preferências locais do aparelho (08 §8.10 `menu/settings`): som, fonte da cozinha, última aba. */
import Storage from 'expo-sqlite/kv-store';
import { create } from 'zustand';
import { createJSONStorage, persist } from 'zustand/middleware';

export type TabName = 'marmitas' | 'salao' | 'pedidos' | 'fechamento';

interface PreferencesState {
  orderSound: boolean;
  kitchenFontScale: number;
  lastTab: TabName | null;
  setOrderSound(on: boolean): void;
  setKitchenFontScale(scale: number): void;
  setLastTab(tab: TabName): void;
}

export const usePreferences = create<PreferencesState>()(
  persist(
    (set) => ({
      orderSound: true,
      kitchenFontScale: 1.2,
      lastTab: null,
      setOrderSound: (orderSound) => set({ orderSound }),
      setKitchenFontScale: (kitchenFontScale) => set({ kitchenFontScale }),
      setLastTab: (lastTab) => set({ lastTab }),
    }),
    { name: 'almoseis.preferences', storage: createJSONStorage(() => Storage) },
  ),
);
