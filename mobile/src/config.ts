import * as Application from 'expo-application';
import { Platform } from 'react-native';

import type { Platform as ApiPlatform } from '@/api/types';

/** 09 §9.4: definido por perfil do EAS; no desenvolvimento, `.env` com o IP da máquina. */
export const API_BASE_URL = (process.env.EXPO_PUBLIC_API_BASE_URL ?? 'http://localhost:5080').replace(/\/+$/, '');

export const APP_VERSION = Application.nativeApplicationVersion ?? '1.0.0';

export const APP_PLATFORM: ApiPlatform = Platform.OS === 'ios' ? 'IOS' : 'ANDROID';

/** Versão dos termos aceitos no cadastro (06 §6.3). */
export const TERMS_VERSION = '2026-10';

export const REQUEST_TIMEOUT_MS = 15_000;
