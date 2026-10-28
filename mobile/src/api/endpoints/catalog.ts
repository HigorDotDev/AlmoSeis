import { apiRequest, qs } from '../client';
import type { CreateProductBody, ImageUpload, Items, MarmitaSize, Product, ProductKind, UpdateProductBody, Uuid } from '../types';

/** 06 §6.5. */
export const productsApi = {
  list: (f: { kind?: ProductKind; includeInactive?: boolean } = {}) => apiRequest<Items<Product>>('GET', `/products${qs(f)}`),
  get: (id: Uuid) => apiRequest<Product>('GET', `/products/${id}`),
  create: (body: CreateProductBody) => apiRequest<Product>('POST', '/products', { body }),
  update: (id: Uuid, body: UpdateProductBody, version: number) =>
    apiRequest<Product>('PATCH', `/products/${id}`, { body, ifMatch: version }),
  imageUpload: (id: Uuid, contentType: string, sizeBytes: number) =>
    apiRequest<ImageUpload>('POST', `/products/${id}/image-upload`, { body: { contentType, sizeBytes } }),
};

export const sizesApi = {
  list: (includeInactive = false) => apiRequest<Items<MarmitaSize>>('GET', `/marmita-sizes${qs({ includeInactive })}`),
  create: (body: { name: string; price: string; sortOrder: number }) => apiRequest<MarmitaSize>('POST', '/marmita-sizes', { body }),
  update: (id: Uuid, body: Partial<{ name: string; price: string; sortOrder: number; isActive: boolean }>, version: number) =>
    apiRequest<MarmitaSize>('PATCH', `/marmita-sizes/${id}`, { body, ifMatch: version }),
};
