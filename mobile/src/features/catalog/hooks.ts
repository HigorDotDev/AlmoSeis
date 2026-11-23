/** Catálogo (06 §6.5). */
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { productsApi, sizesApi, type MarmitaSizeBody } from '@/api/endpoints/catalog';
import { qk } from '@/api/queryKeys';
import type { CreateProductBody, Product, ProductKind, UpdateProductBody } from '@/api/types';

import { uploadJpeg } from './imageUpload';

const STALE = 10 * 60_000;

export function useProducts(f: { kind?: ProductKind; includeInactive?: boolean } = {}) {
  return useQuery({ queryKey: qk.products(f), queryFn: async () => (await productsApi.list(f)).data.items, staleTime: STALE });
}

export function useProduct(id: string | undefined) {
  return useQuery({
    queryKey: qk.product(id ?? ''),
    queryFn: async () => (await productsApi.get(id!)).data,
    enabled: !!id,
    staleTime: STALE,
  });
}

export function useMarmitaSizes(includeInactive = false, enabled = true) {
  return useQuery({
    queryKey: qk.marmitaSizes(includeInactive),
    queryFn: async () => (await sizesApi.list(includeInactive)).data.items.sort((a, b) => a.sortOrder - b.sortOrder),
    enabled,
    staleTime: STALE,
  });
}

function setProduct(client: ReturnType<typeof useQueryClient>, product: Product) {
  client.setQueryData(qk.product(product.id), product);
  void client.invalidateQueries({ queryKey: ['products'] });
  void client.invalidateQueries({ queryKey: ['menus'] });
}

export function useCreateProduct() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async (body: CreateProductBody) => (await productsApi.create(body)).data,
    onSuccess: (p) => setProduct(client, p),
  });
}

export function useUpdateProduct() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, body, version }: { id: string; body: UpdateProductBody; version: number }) =>
      (await productsApi.update(id, body, version)).data,
    onSuccess: (p) => setProduct(client, p),
  });
}

export function useSaveSize() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ id, version, isActive, ...body }: MarmitaSizeBody & { id?: string; version?: number; isActive?: boolean }) =>
      id ? (await sizesApi.update(id, { ...body, isActive }, version!)).data : (await sizesApi.create(body)).data,
    onSuccess: () => client.invalidateQueries({ queryKey: ['marmitaSizes'] }),
  });
}

/** Foto do produto (08 §8.10, `imageUpload.ts`): envia ao R2 e grava a chave com `PATCH`. */
export function useUploadProductImage() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ product, uri }: { product: Product; uri: string }) => {
      const storageKey = await uploadJpeg(uri, (contentType, sizeBytes) => productsApi.imageUpload(product.id, contentType, sizeBytes));
      return (await productsApi.update(product.id, { imageStorageKey: storageKey }, product.version)).data;
    },
    onSuccess: (p) => setProduct(client, p),
  });
}
