/** Tokens de cor claro/escuro com contraste AA (08 §8.11). */
import { useColorScheme } from 'react-native';

const light = {
  bg: '#F6F5F2',
  surface: '#FFFFFF',
  surfaceAlt: '#EFEDE8',
  border: '#D9D5CC',
  text: '#1C1B19',
  textMuted: '#5E5A52',
  primary: '#B4410E',
  primaryText: '#FFFFFF',
  danger: '#B42318',
  dangerBg: '#FDECEA',
  success: '#1E7A3C',
  successBg: '#E6F4EA',
  warning: '#8A5A00',
  warningBg: '#FFF4D6',
  info: '#1D4F91',
  infoBg: '#E5EEFA',
  neutral: '#57534E',
  neutralBg: '#ECEAE6',
  alert: '#D9480F',
  overlay: 'rgba(0,0,0,0.45)',
};

const dark: typeof light = {
  bg: '#141311',
  surface: '#1F1E1B',
  surfaceAlt: '#2A2925',
  border: '#3A3833',
  text: '#F3F1EC',
  textMuted: '#B3AEA4',
  primary: '#F08A4B',
  primaryText: '#1C1B19',
  danger: '#F97066',
  dangerBg: '#3A1714',
  success: '#5FD18A',
  successBg: '#143120',
  warning: '#F5C451',
  warningBg: '#3A2E0E',
  info: '#84ADEB',
  infoBg: '#14243B',
  neutral: '#C8C3BA',
  neutralBg: '#2E2C28',
  alert: '#FF8A4C',
  overlay: 'rgba(0,0,0,0.6)',
};

export type Colors = typeof light;

export function useColors(): Colors {
  return useColorScheme() === 'dark' ? dark : light;
}

export const space = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24 } as const;
export const radius = { sm: 6, md: 10, lg: 16, pill: 999 } as const;
export const font = { sm: 13, md: 15, lg: 17, xl: 20, xxl: 26 } as const;
/** Alvo de toque mínimo (RNF-001). */
export const TOUCH = 48;
