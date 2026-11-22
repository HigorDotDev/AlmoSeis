/**
 * Foto de produto, prato ou combo (08 §8.10, 06 §6.5): reduz para 1024 px JPEG 80% → pede a URL ao servidor →
 * `PUT` direto no R2 → devolve a `storageKey`, que a tela grava com `PATCH imageStorageKey` (com `If-Match`).
 * O arquivo anterior é apagado pelo servidor só depois do `PATCH`.
 */
import type { ApiResult } from '@/api/client';
import type { ImageUpload } from '@/api/types';

export async function uploadJpeg(uri: string, requestUpload: (contentType: string, sizeBytes: number) => Promise<ApiResult<ImageUpload>>): Promise<string> {
  const { ImageManipulator, SaveFormat } = await import('expo-image-manipulator');
  const context = ImageManipulator.manipulate(uri);
  context.resize({ width: 1024 });
  const rendered = await context.renderAsync();
  const saved = await rendered.saveAsync({ compress: 0.8, format: SaveFormat.JPEG });
  const blob = await (await fetch(saved.uri)).blob();
  const upload = (await requestUpload('image/jpeg', blob.size)).data;
  const put = await fetch(upload.uploadUrl, { method: upload.method, headers: upload.headers, body: blob });
  if (!put.ok) {
    throw new Error(`Falha no envio da imagem (${put.status}).`);
  }
  return upload.storageKey;
}
