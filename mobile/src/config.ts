import * as Application from 'expo-application';
import { Platform } from 'react-native';

import type { Platform as ApiPlatform } from '@/api/types';

export { PUBLIC_PAGE_BASE_URL, REQUEST_TIMEOUT_MS, TERMS_VERSION } from './configShared';

/** 09 §9.4: definido por perfil do EAS; no desenvolvimento, `.env` com o IP da máquina. */
export const API_BASE_URL = (process.env.EXPO_PUBLIC_API_BASE_URL ?? 'http://localhost:5080').replace(/\/+$/, '');

export const APP_VERSION = Application.nativeApplicationVersion ?? '1.0.0';

export const APP_PLATFORM: ApiPlatform = Platform.OS === 'ios' ? 'IOS' : 'ANDROID';
