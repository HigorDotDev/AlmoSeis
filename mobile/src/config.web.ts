/** Web desktop (ADR-0053 §6): plataforma `WEB` e versão vinda do build (`expo.version` do app.json). */
import Constants from 'expo-constants';

import type { Platform as ApiPlatform } from '@/api/types';

export { PUBLIC_PAGE_BASE_URL, REQUEST_TIMEOUT_MS, TERMS_VERSION } from './configShared';

/**
 * A API precisa ser do mesmo *site* do app web para o cookie `SameSite=Strict` seguir (ADR-0053 §2). No
 * desenvolvimento o celular usa o IP da máquina (`EXPO_PUBLIC_API_BASE_URL`), mas o navegador em
 * `localhost:8082` precisa de `localhost:5080`: por isso a variável própria da web vem primeiro.
 */
export const API_BASE_URL = (process.env.EXPO_PUBLIC_WEB_API_BASE_URL ?? process.env.EXPO_PUBLIC_API_BASE_URL ?? 'http://localhost:5080').replace(/\/+$/, '');

export const APP_VERSION = Constants.expoConfig?.version ?? '1.0.0';

export const APP_PLATFORM: ApiPlatform = 'WEB';
