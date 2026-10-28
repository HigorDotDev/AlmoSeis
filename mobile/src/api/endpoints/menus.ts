import { apiRequest, qs } from '../client';
import type { CloneMenuBody, CreateMenuBody, Items, Menu, MenuItemInput, MenuKind, MenuSummary, MenuType, Uuid } from '../types';

/** 06 §6.7. */
export const menusApi = {
  current: (menuType: MenuType) => apiRequest<Menu>('GET', `/menus/current?menuType=${menuType}`),
  list: (f: { menuType?: MenuType; kind?: MenuKind; from?: string; to?: string }) =>
    apiRequest<Items<MenuSummary>>('GET', `/menus${qs(f)}`),
  get: (id: Uuid) => apiRequest<Menu>('GET', `/menus/${id}`),
  create: (body: CreateMenuBody) => apiRequest<Menu>('POST', '/menus', { body }),
  replaceItems: (id: Uuid, items: MenuItemInput[], version: number) =>
    apiRequest<Menu>('PUT', `/menus/${id}/items`, { body: { items }, ifMatch: version }),
  updateItem: (id: Uuid, itemId: Uuid, body: { isAvailable?: boolean; pfPrice?: string | null }, version: number) =>
    apiRequest<Menu>('PATCH', `/menus/${id}/items/${itemId}`, { body, ifMatch: version }),
  clone: (id: Uuid, body: CloneMenuBody) => apiRequest<Menu>('POST', `/menus/${id}/clone`, { body }),
};
