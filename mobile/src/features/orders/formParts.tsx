/** Peças dos formulários de pedido: oferta de rascunho, bebidas, adicionais, rodapé com prévia. */
import { View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import type { Product } from '@/api/types';
import { Banner, Button, Card, QuantityStepper, Row, SyncStatePill, Text, space, useColors } from '@/components/ui';
import type { Draft } from '@/db/schema';
import { formatBRL } from '@/lib/money';
import { formatTime } from '@/lib/dates';

import type { AddonLine } from './compose';

export function DraftOffer({ offer, timeZone, onResume, onDiscard }: { offer: Draft | null; timeZone: string; onResume: () => void; onDiscard: () => void }) {
  if (!offer) return null;
  return (
    <Card>
      <Text>Continuar rascunho de {formatTime(offer.updatedAt, timeZone)}?</Text>
      <Row>
        <Button title="Continuar" onPress={onResume} style={{ flex: 1 }} />
        <Button title="Descartar" variant="secondary" onPress={onDiscard} style={{ flex: 1 }} />
      </Row>
    </Card>
  );
}

export function BeverageGrid({ beverages, value, onChange }: { beverages: Product[]; value: Record<string, number>; onChange: (next: Record<string, number>) => void }) {
  if (beverages.length === 0) return <Text muted>Nenhuma bebida disponível.</Text>;
  return (
    <View style={{ gap: space.sm }}>
      {beverages.map((b) => (
        <Row key={b.id} style={{ justifyContent: 'space-between' }}>
          <View style={{ flex: 1 }}>
            <Text>{b.name}</Text>
            <Text variant="small" muted>
              {b.defaultPrice ? formatBRL(b.defaultPrice) : '—'}
            </Text>
          </View>
          <QuantityStepper label={b.name} value={value[b.id] ?? 0} onChange={(q) => onChange({ ...value, [b.id]: q })} />
        </Row>
      ))}
    </View>
  );
}

export function AddonPicker({ addons, value, onChange }: { addons: Product[]; value: AddonLine[]; onChange: (next: AddonLine[]) => void }) {
  if (addons.length === 0) return null;
  const qty = (id: string) => value.find((a) => a.productId === id)?.quantity ?? 0;
  const set = (id: string, q: number) => onChange([...value.filter((a) => a.productId !== id), ...(q > 0 ? [{ productId: id, quantity: q }] : [])]);
  return (
    <View style={{ gap: space.sm }}>
      <Text variant="small" muted>
        Adicionais (por unidade)
      </Text>
      {addons.map((a) => (
        <Row key={a.id} style={{ justifyContent: 'space-between' }}>
          <Text style={{ flex: 1 }}>
            {a.name} {a.defaultPrice ? `· ${formatBRL(a.defaultPrice)}` : ''}
          </Text>
          <QuantityStepper label={a.name} value={qty(a.id)} max={9} onChange={(q) => set(a.id, q)} />
        </Row>
      ))}
    </View>
  );
}

/** Rodapé fixo: "Prévia: R$ 55,00" e o botão de registrar. Offline o formulário vira rascunho (ADR-0010). */
export function SubmitFooter({
  previewCents,
  label,
  disabled,
  loading,
  online,
  unknown,
  error,
  onPress,
}: {
  previewCents: number;
  label: string;
  disabled?: boolean;
  loading?: boolean;
  online: boolean;
  unknown?: boolean;
  error?: string | null;
  onPress: () => void;
}) {
  const c = useColors();
  const insets = useSafeAreaInsets();
  return (
    <View style={{ backgroundColor: c.surface, borderTopWidth: 1, borderTopColor: c.border, padding: space.md, paddingBottom: space.md + insets.bottom, gap: space.sm }}>
      {error ? <Banner tone="danger" text={error} /> : null}
      {unknown ? <Banner tone="warning" text="Não sabemos se o pedido foi registrado. Verificando…" /> : null}
      <Row style={{ justifyContent: 'space-between' }}>
        <Text muted>Prévia</Text>
        <Text variant="title">{formatBRL(previewCents)}</Text>
      </Row>
      {!online ? (
        <Row>
          <SyncStatePill state="draft" />
          <Text variant="small" muted style={{ flex: 1 }}>
            Sem internet: o pedido fica salvo como rascunho e pode ser enviado quando a conexão voltar.
          </Text>
        </Row>
      ) : null}
      <Button title={label} size="large" onPress={onPress} disabled={disabled || !online || unknown} loading={loading} />
    </View>
  );
}
