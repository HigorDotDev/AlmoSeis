import { apiRequest } from '../client';
import type { Storefront, UpdateStorefrontBody } from '../types';

/** 06 §6.18: configuração da página de pedidos pelo restaurante. Ler: `orders.view`; gravar: `storefront.manage` [M]. */
export const storefrontApi = {
  get: () => apiRequest<Storefront>('GET', '/storefront'),
  update: (body: UpdateStorefrontBody, version: number) => apiRequest<Storefront>('PUT', '/storefront', { body, ifMatch: version }),
};
