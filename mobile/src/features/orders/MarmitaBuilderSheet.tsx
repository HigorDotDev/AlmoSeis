/**
 * Montagem de uma marmita (08 §8.9): tamanho, pratos e acompanhamentos do dia, adicionais, quantidade e observação.
 * O estado nasce de `initial`: quem usa remonta pelo `key` a cada abertura.
 */
import { useState } from 'react';
import { View } from 'react-native';

import type { MarmitaSize, Menu, Product } from '@/api/types';
import { BottomSheet, Button, Chip, QuantityStepper, Row, Text, TextField } from '@/components/ui';
import { formatBRL } from '@/lib/money';
import { newKey } from '@/lib/ids';

import type { MarmitaLine } from './compose';
import { AddonPicker } from './formParts';

export function MarmitaBuilderSheet({
  visible,
  initial,
  sizes,
  menu,
  addons,
  onSave,
  onClose,
}: {
  visible: boolean;
  initial: MarmitaLine | null;
  sizes: MarmitaSize[];
  menu: Menu | null;
  addons: Product[];
  onSave: (line: MarmitaLine) => void;
  onClose: () => void;
}) {
  const blank = (): MarmitaLine => ({ key: newKey(), sizeId: sizes[0]?.id ?? '', quantity: 1, mainDishIds: [], sideDishIds: [], notes: '', addons: [] });
  const [line, setLine] = useState<MarmitaLine>(initial ?? blank());

  const toggle = (field: 'mainDishIds' | 'sideDishIds', id: string) =>
    setLine((l) => ({ ...l, [field]: l[field].includes(id) ? l[field].filter((x) => x !== id) : [...l[field], id] }));

  const items = menu?.items ?? [];
  const mains = items.filter((i) => i.section === 'MAIN_DISH');
  const sides = items.filter((i) => i.section === 'SIDE_DISH');

  return (
    <BottomSheet
      visible={visible}
      title={initial ? 'Editar marmita' : 'Adicionar marmita'}
      onClose={onClose}
      footer={
        <Button
          title={initial ? 'Salvar marmita' : 'Adicionar ao pedido'}
          disabled={!line.sizeId || line.mainDishIds.length === 0}
          onPress={() => {
            onSave(line);
            onClose();
          }}
        />
      }
    >
      <Text variant="subtitle">Tamanho</Text>
      <Row style={{ flexWrap: 'wrap' }}>
        {sizes.map((s) => (
          <Chip key={s.id} label={`${s.name} · ${formatBRL(s.price)}`} selected={line.sizeId === s.id} onPress={() => setLine({ ...line, sizeId: s.id })} />
        ))}
      </Row>
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
