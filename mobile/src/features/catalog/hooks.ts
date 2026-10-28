/** Catálogo (06 §6.5). */
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';

import { productsApi, sizesApi } from '@/api/endpoints/catalog';
import { qk } from '@/api/queryKeys';
import type { CreateProductBody, Product, ProductKind, UpdateProductBody } from '@/api/types';

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

export function useMarmitaSizes(includeInactive = false) {
  return useQuery({
    queryKey: qk.marmitaSizes(includeInactive),
    queryFn: async () => (await sizesApi.list(includeInactive)).data.items.sort((a, b) => a.sortOrder - b.sortOrder),
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
    mutationFn: async (v: { id?: string; version?: number; name: string; price: string; sortOrder: number; isActive?: boolean }) =>
      v.id
        ? (await sizesApi.update(v.id, { name: v.name, price: v.price, sortOrder: v.sortOrder, isActive: v.isActive }, v.version!)).data
        : (await sizesApi.create({ name: v.name, price: v.price, sortOrder: v.sortOrder })).data,
    onSuccess: () => client.invalidateQueries({ queryKey: ['marmitaSizes'] }),
  });
}

/**
 * Foto do produto (08 §8.10): reduz para 1024 px JPEG 80% → pede a URL → PUT direto no R2 → PATCH com a chave.
 * O arquivo anterior é apagado pelo servidor só depois do PATCH (06 §6.5).
 */
export function useUploadProductImage() {
  const client = useQueryClient();
  return useMutation({
    mutationFn: async ({ product, uri }: { product: Product; uri: string }) => {
      const { ImageManipulator, SaveFormat } = await import('expo-image-manipulator');
      const context = ImageManipulator.manipulate(uri);
      context.resize({ width: 1024 });
      const rendered = await context.renderAsync();
      const saved = await rendered.saveAsync({ compress: 0.8, format: SaveFormat.JPEG });
      const blob = await (await fetch(saved.uri)).blob();
      const upload = (await productsApi.imageUpload(product.id, 'image/jpeg', blob.size)).data;
      const put = await fetch(upload.uploadUrl, { method: upload.method, headers: upload.headers, body: blob });
      if (!put.ok) {
        throw new Error(`Falha no envio da imagem (${put.status}).`);
      }
      return (await productsApi.update(product.id, { imageStorageKey: upload.storageKey }, product.version)).data;
    },
    onSuccess: (p) => setProduct(client, p),
  });
}
