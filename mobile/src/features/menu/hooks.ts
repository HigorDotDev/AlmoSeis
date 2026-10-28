/** Cardápios (06 §6.7, ADR-0038). */
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { isApiError } from '@/api/errors';
import { menusApi } from '@/api/endpoints/menus';
import { qk } from '@/api/queryKeys';
import type { CloneMenuBody, CreateMenuBody, Menu, MenuItemInput, MenuKind, MenuSources, MenuType } from '@/api/types';

const STALE = 2 * 60_000;

export type CurrentMenu = { menu: Menu; missing: null } | { menu: null; missing: { sources: MenuSources; businessDate: string | null } };

/** 404 MenuNotFound vira `missing` com as opções de reaproveitamento (RN-003.2). */
export function useCurrentMenu(menuType: MenuType, enabled = true) {
  return useQuery({
    queryKey: qk.currentMenu(menuType),
    queryFn: async (): Promise<CurrentMenu> => {
      try {
        return { menu: (await menusApi.current(menuType)).data, missing: null };
      } catch (e) {
        if (isApiError(e, 'MenuNotFound')) {
          const sources = (e.extensions.sources as MenuSources | undefined) ?? { yesterdayMenuId: null, lastWeekSameDayMenuId: null, weeklyTemplateId: null };
          const businessDate = typeof e.extensions.businessDate === 'string' ? e.extensions.businessDate : null;
          return { menu: null, missing: { sources, businessDate } };
        }
        throw e;
      }
    },
    staleTime: STALE,
    enabled,
  });
}

export function useMenu(id: string | undefined) {
  return useQuery({ queryKey: qk.menu(id ?? ''), queryFn: async () => (await menusApi.get(id!)).data, enabled: !!id, staleTime: STALE });
}

export function useMenus(f: { menuType?: MenuType; kind?: MenuKind; from?: string; to?: string }) {
  return useQuery({ queryKey: qk.menuList(f), queryFn: async () => (await menusApi.list(f)).data.items, staleTime: STALE });
}

function setMenu(client: ReturnType<typeof useQueryClient>, menu: Menu) {
  client.setQueryData(qk.menu(menu.id), menu);
  void client.invalidateQueries({ queryKey: ['menus'] });
}

export function useCreateMenu() {
  const client = useQueryClient();
  return useMutation({ mutationFn: async (body: CreateMenuBody) => (await menusApi.create(body)).data, onSuccess: (m) => setMenu(client, m) });
}

export function useCloneMenu() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, body }: { id: string; body: CloneMenuBody }) => (await menusApi.clone(id, body)).data,
    onSuccess: (m) => setMenu(client, m),
  });
}

export function useReplaceMenuItems() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, items, version }: { id: string; items: MenuItemInput[]; version: number }) =>
      (await menusApi.replaceItems(id, items, version)).data,
    onSuccess: (m) => setMenu(client, m),
  });
}

/** "Acabou a costela" e preço de PF. */
export function useUpdateMenuItem() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, itemId, body, version }: { id: string; itemId: string; body: { isAvailable?: boolean; pfPrice?: string | null }; version: number }) =>
      (await menusApi.updateItem(id, itemId, body, version)).data,
    onSuccess: (m) => setMenu(client, m),
  });
}

export function toItemInputs(menu: Menu): MenuItemInput[] {
  return menu.items.map((i) => ({ productId: i.productId, pfPrice: i.pfPrice, isAvailable: i.isAvailable, sortOrder: i.sortOrder }));
}
