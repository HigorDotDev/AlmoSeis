/** Chave "Vende no site" + preço de site (ADR-0057 item 1), com a aparência da chave de disponibilidade. */
import { Switch, View } from 'react-native';

import { MoneyInput, Row, Text } from '@/components/ui';
import type { MoneyString } from '@/lib/money';
import { toggleSiteSale, type SiteSale } from '@/lib/sitePrice';

/**
 * "Vende no site" (ADR-0057 item 1), com a aparência da chave de disponibilidade. Ligada, mostra o preço de site,
 * preenchido com o de balcão quando vazio (item 2). Vazio = o servidor grava o preço de balcão.
 */
export function SiteSaleField({
  value,
  counterPrice,
  onChange,
  editable = true,
  error,
  name,
}: {
  value: SiteSale;
  counterPrice: MoneyString;
  onChange: (v: SiteSale) => void;
  editable?: boolean;
  error?: string | null;
  /** Para o rótulo acessível quando há vários na tela ("Vende no site: 300 ml"). */
  name?: string;
}) {
  const label = name ? `Vende no site: ${name}` : 'Vende no site';
  return (
    <View style={{ gap: 4 }}>
      <Row style={{ justifyContent: 'space-between', minHeight: 48 }}>
        <View style={{ flex: 1 }}>
          <Text style={{ fontWeight: '600' }}>Vende no site</Text>
          <Text variant="small" muted>
            {value.sellsOnSite ? 'Aparece na página de pedidos pelo preço de site.' : 'Não aparece na página de pedidos.'}
          </Text>
        </View>
        <Switch value={value.sellsOnSite} disabled={!editable} accessibilityLabel={label} onValueChange={(on) => onChange(toggleSiteSale(value, on, counterPrice))} />
      </Row>
      {value.sellsOnSite ? (
        <>
          <MoneyInput
            label={name ? `Preço no site: ${name}` : 'Preço no site'}
            value={value.sitePrice}
            editable={editable}
            onChange={(sitePrice) => onChange({ ...value, sitePrice })}
            error={error}
          />
          <Text variant="small" muted>
            Pode ser maior, menor ou igual ao do balcão. Vazio, vale o preço do balcão.
          </Text>
        </>
      ) : null}
    </View>
  );
}
