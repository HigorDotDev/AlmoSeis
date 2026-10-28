/** Tokens de cor claro/escuro com contraste AA (08 §8.11; proposta em docs/10-ux.md §10.4). */
import { useColorScheme } from 'react-native';

const light = {
  bg: '#F6F5F2',
  surface: '#FFFFFF',
  surfaceAlt: '#EFEDE8',
  border: '#D9D5CC',
  text: '#1C1B19',
  textMuted: '#5E5A52',
  primary: '#B23C0B',
  /** Fundo de item selecionado (chip, forma de pagamento). */
  primaryBg: '#FBE9E0',
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
  primaryBg: '#3A2216',
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

export const space = { xs: 4, sm: 8, md: 12, lg: 16, xl: 24, xxl: 32 } as const;
export const radius = { sm: 8, md: 12, lg: 20, pill: 999 } as const;
/** `display`: valores e nº do pedido lidos de longe (saldo, cozinha). */
export const font = { sm: 14, md: 16, lg: 18, xl: 22, xxl: 28, display: 36 } as const;
/** Alvo de toque mínimo (RNF-001): vale para botão, chip, segmentado e item de lista. */
export const TOUCH = 48;
/** Botão principal de rodapé (registrar, receber). */
export const TOUCH_LARGE = 56;
