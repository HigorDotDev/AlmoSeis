/** Chaves do TanStack Query e mapa entityType → invalidação (08 §8.3). */
import type { QueryClient, QueryKey } from '@tanstack/react-query';

import type { EntityType, MenuType, OrderFilters, ProductKind } from './types';

export const qk = {
  me: ['me'] as const,
  businessDay: ['businessDay', 'current'] as const,
  settings: ['settings'] as const,
  tenant: ['tenant'] as const,
  subscription: ['subscription'] as const,
  roles: ['roles'] as const,
  products: (f: { kind?: ProductKind; includeInactive?: boolean } = {}) => ['products', f] as const,
  product: (id: string) => ['products', 'detail', id] as const,
  marmitaSizes: (includeInactive = false) => ['marmitaSizes', includeInactive] as const,
  currentMenu: (menuType: MenuType) => ['menus', 'current', menuType] as const,
  menu: (id: string) => ['menus', id] as const,
  menuList: (f: object) => ['menus', 'list', f] as const,
  orders: (f: OrderFilters) => ['orders', 'list', f] as const,
  order: (id: string) => ['orders', id] as const,
  orderAlerts: (id: string) => ['orders', id, 'alerts'] as const,
  orderHistory: (id: string) => ['orders', id, 'history'] as const,
  orderPayments: (id: string) => ['orders', id, 'payments'] as const,
  production: (businessDayId: string | undefined) => ['production', businessDayId ?? 'current'] as const,
  customerSearch: (term: string) => ['customers', 'search', term] as const,
  customer: (id: string) => ['customers', id] as const,
  customerPending: (id: string) => ['customers', id, 'pending'] as const,
  closingSummary: (businessDayId?: string) => ['closing', 'summary', businessDayId ?? 'current'] as const,
  closingHistory: (f: object) => ['closing', 'history', f] as const,
  closing: (id: string) => ['closing', id] as const,
  closingMonthly: (month: string) => ['closing', 'monthly', month] as const,
  cashMovements: (businessDayId?: string) => ['closing', 'cashMovements', businessDayId ?? 'current'] as const,
  employees: (status?: string) => ['employees', status ?? 'all'] as const,
  employee: (id: string) => ['employees', 'detail', id] as const,
  invitations: ['employees', 'invitations'] as const,
  devices: ['devices'] as const,
  audit: (f: object) => ['audit', f] as const,
};

/** Prefixos a invalidar por entityType (08 §8.3). `entityId` refina o que for por id. */
export function keysForEntity(entityType: EntityType, entityId?: string | null, ownEmployeeId?: string | null): QueryKey[] {
  switch (entityType) {
    case 'ORDER':
      return [...(entityId ? [['orders', entityId]] : []), ['orders', 'list'], ['production'], ['customers']];
    case 'PAYMENT':
    case 'PAYMENT_REVERSAL':
      // O pedido do pagamento não vem no evento: invalida os detalhes de pedido em geral.
      return [['orders'], ['closing', 'summary']];
    case 'MENU':
      return [['menus']];
    case 'PRODUCT':
    case 'MARMITA_SIZE':
    case 'TENANT_SETTINGS':
      return [['products'], ['marmitaSizes'], ['settings']];
    case 'CUSTOMER':
      return [['customers']];
    case 'EMPLOYEE':
      return [...(entityId && entityId === ownEmployeeId ? [['me']] : []), ['employees']];
    case 'BUSINESS_DAY':
    case 'CASH_MOVEMENT':
    case 'DAILY_CLOSING':
    case 'CLOSING_ADJUSTMENT':
      return [['businessDay'], ['closing']];
    default:
      return [];
  }
}

export function invalidateEntity(
  client: QueryClient,
  entityType: EntityType,
  entityId?: string | null,
  ownEmployeeId?: string | null,
): void {
  for (const queryKey of keysForEntity(entityType, entityId, ownEmployeeId)) {
    void client.invalidateQueries({ queryKey });
  }
}
