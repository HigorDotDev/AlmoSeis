/**
 * Datas (08 §8.12). Data operacional ("2026-10-27") não passa por fuso: é só reformatada.
 * Instantes UTC são mostrados no fuso do estabelecimento (`tenant.timeZone`).
 */
export function formatBusinessDate(date: string, withYear = false): string {
  const [y, m, d] = date.split('-');
  return withYear ? `${d}/${m}/${y}` : `${d}/${m}`;
}

export function toLocal(instant: string, timeZone: string): { date: string; time: string } {
  const parts = new Intl.DateTimeFormat('pt-BR', {
    timeZone,
    day: '2-digit',
    month: '2-digit',
    year: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).formatToParts(new Date(instant));
  const get = (type: Intl.DateTimeFormatPartTypes) => parts.find((p) => p.type === type)?.value ?? '';
  return { date: `${get('day')}/${get('month')}/${get('year')}`, time: `${get('hour')}:${get('minute')}` };
}

export function formatTime(instant: string, timeZone: string): string {
  return toLocal(instant, timeZone).time;
}

export function formatDateTime(instant: string, timeZone: string): string {
  const { date, time } = toLocal(instant, timeZone);
  return `${date.slice(0, 5)} ${time}`;
}

/** "agora", "há 12 min", "há 2 h", "há 3 dias". */
export function relativeTime(instant: string, now: Date = new Date()): string {
  const minutes = Math.floor((now.getTime() - new Date(instant).getTime()) / 60_000);
  if (minutes < 1) return 'agora';
  if (minutes < 60) return `há ${minutes} min`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `há ${hours} h`;
  const days = Math.floor(hours / 24);
  return days === 1 ? 'há 1 dia' : `há ${days} dias`;
}

/** Data de hoje no fuso do estabelecimento, como data operacional ("2026-10-27"). */
export function todayIn(timeZone: string, now: Date = new Date()): string {
  const parts = new Intl.DateTimeFormat('en-CA', { timeZone, year: 'numeric', month: '2-digit', day: '2-digit' }).format(now);
  return parts;
}

/** Soma dias a uma data operacional, sem fuso. */
export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T12:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** ISO, como o `dayOfWeek` dos cardápios: 1 = segunda … 7 = domingo. */
export function isoDayOfWeek(date: string): number {
  return new Date(`${date}T12:00:00Z`).getUTCDay() || 7;
}

/** Indexado pelo dia ISO (posição 0 sem uso). */
export const WEEKDAYS = ['', 'Segunda', 'Terça', 'Quarta', 'Quinta', 'Sexta', 'Sábado', 'Domingo'] as const;

/** "2026-10" → "outubro de 2026". */
export function formatMonth(month: string): string {
  const [y, m] = month.split('-');
  const names = ['janeiro', 'fevereiro', 'março', 'abril', 'maio', 'junho', 'julho', 'agosto', 'setembro', 'outubro', 'novembro', 'dezembro'];
  return `${names[Number(m) - 1]} de ${y}`;
}
