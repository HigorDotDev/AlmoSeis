/**
 * Montagem de uma marmita (08 §8.9): tamanho, pratos e acompanhamentos do dia, adicionais, quantidade e observação.
 * O estado nasce de `initial`: quem usa remonta pelo `key` a cada abertura.
 */
import { router } from 'expo-router';
import { useState } from 'react';
import { View } from 'react-native';

import type { MarmitaSize, Menu, Product } from '@/api/types';
import { Banner, BottomSheet, Button, Chip, QuantityStepper, Row, Text, TextField } from '@/components/ui';
import { formatBRL } from '@/lib/money';
import { newKey } from '@/lib/ids';
import { usePermission } from '@/stores/session';

import type { MarmitaLine } from './compose';
import { AddonPicker } from './formParts';

export function MarmitaBuilderSheet({
  visible,
  initial,
  initialSizeId,
  sizes,
  menu,
  addons,
  onSave,
  onClose,
}: {
  visible: boolean;
  initial: MarmitaLine | null;
  /** Tamanho tocado na tela (marmita primeiro, 08 §8.9). */
  initialSizeId?: string | null;
  sizes: MarmitaSize[];
  menu: Menu | null;
  addons: Product[];
  onSave: (line: MarmitaLine) => void;
  onClose: () => void;
}) {
  const blank = (): MarmitaLine => ({ key: newKey(), sizeId: initialSizeId ?? sizes[0]?.id ?? '', quantity: 1, mainDishIds: [], sideDishIds: [], notes: '', addons: [] });
  const [line, setLine] = useState<MarmitaLine>(initial ?? blank());
  const canManageCatalog = usePermission('catalog.manage');

  const toggle = (field: 'mainDishIds' | 'sideDishIds', id: string) =>
    setLine((l) => ({ ...l, [field]: l[field].includes(id) ? l[field].filter((x) => x !== id) : [...l[field], id] }));

  const items = menu?.items ?? [];
  const mains = items.filter((i) => i.section === 'MAIN_DISH');
  const sides = items.filter((i) => i.section === 'SIDE_DISH');
  const missing = !line.sizeId ? 'Escolha o tamanho.' : line.mainDishIds.length === 0 ? 'Escolha o prato principal.' : null;

  return (
    <BottomSheet
      visible={visible}
      title={initial ? 'Editar marmita' : 'Adicionar marmita'}
      onClose={onClose}
      footer={
        <View style={{ gap: 4 }}>
          {/* O botão desabilitado diz o que falta (docs/10-ux.md D3). */}
          {missing ? (
            <Text variant="small" muted style={{ textAlign: 'center' }}>
              {missing}
            </Text>
          ) : null}
          <Button
            title={initial ? 'Salvar marmita' : 'Adicionar ao pedido'}
            size="large"
            disabled={!!missing}
            onPress={() => {
              onSave(line);
              onClose();
            }}
          />
        </View>
      }
    >
      <Text variant="subtitle">Tamanho</Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {sizes.map((s) => (
          <Chip key={s.id} label={`${s.name} · ${formatBRL(s.price)}`} selected={line.sizeId === s.id} onPress={() => setLine({ ...line, sizeId: s.id })} />
        ))}
      </Row>
      {sizes.length === 0 ? (
        // O preço da marmita vem do tamanho (ADR-0020): sem tamanho cadastrado não dá para montar o pedido.
        <Banner
          tone="warning"
          text={
            canManageCatalog
              ? 'Nenhum tamanho de marmita cadastrado. O preço da marmita vem do tamanho (ex.: P, M, G).'
              : 'Nenhum tamanho de marmita cadastrado. Peça ao proprietário ou gerente para cadastrar.'
          }
          action={
            canManageCatalog
              ? {
                  label: 'Cadastrar tamanhos',
                  onPress: () => {
                    onClose();
                    router.push('/menu/sizes');
                  },
                }
              : undefined
          }
        />
      ) : null}
      <Text variant="subtitle">Prato principal</Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {mains.map((m) => (
          <Chip key={m.id} label={m.name} strike={!m.isAvailable} disabled={!m.isAvailable} selected={line.mainDishIds.includes(m.productId)} onPress={() => toggle('mainDishIds', m.productId)} />
        ))}
        {mains.length === 0 ? <Text muted>Nenhum prato no cardápio de hoje.</Text> : null}
      </Row>
      <Text variant="subtitle">Acompanhamentos</Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {sides.map((m) => (
          <Chip key={m.id} label={m.name} strike={!m.isAvailable} disabled={!m.isAvailable} selected={line.sideDishIds.includes(m.productId)} onPress={() => toggle('sideDishIds', m.productId)} />
        ))}
      </Row>
      <AddonPicker addons={addons} value={line.addons} onChange={(a) => setLine({ ...line, addons: a })} />
      <Row style={{ justifyContent: 'space-between' }}>
        <Text variant="subtitle">Quantidade</Text>
        <QuantityStepper label="Quantidade de marmitas" min={1} value={line.quantity} onChange={(quantity) => setLine({ ...line, quantity })} />
      </Row>
      <View>
        <TextField label="Observação" value={line.notes} onChangeText={(notes) => setLine({ ...line, notes })} placeholder="Ex.: sem cebola" />
      </View>
    </BottomSheet>
  );
}
