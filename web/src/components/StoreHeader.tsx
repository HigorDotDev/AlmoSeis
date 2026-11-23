import type { PublicStore } from '@/lib/api/types';
import { describeDeliveryRule } from '@/lib/delivery';
import { pageTabs, storeSections } from '@/lib/fixed-menu';
import { formatE164, whatsappLink } from '@/lib/phone';
import { formatAddress, STORE_STATUS_TEXT } from '@/lib/texts';

export function StoreHeader({ store }: { store: PublicStore }) {
  const address = formatAddress(store.address);
  const phone = formatE164(store.contactPhone);
  const deliveryRule = describeDeliveryRule(store.deliveryRule);
  return (
    <header className="store-header">
      <h1>{store.name}</h1>
      {address && <p className="muted small">{address}</p>}
      {store.hoursText && <p className="muted small">Horário: {store.hoursText}</p>}
      {phone && store.contactPhone && (
        <p className="small">
          Contato:{' '}
          <a href={whatsappLink(store.contactPhone)} rel="noopener noreferrer" target="_blank">
            {phone} (WhatsApp)
          </a>
        </p>
      )}
      {store.status === 'OPEN' && (
        <p className="small">
          {[store.pickupAvailable && 'Retirada no local', store.deliveryAvailable && 'Entrega'].filter(Boolean).join(' · ')}
          {store.deliveryAvailable && deliveryRule && <span className="muted"> ({deliveryRule})</span>}
        </p>
      )}
    </header>
  );
}

export function StoreStatusNotice({ store }: { store: PublicStore }) {
  if (store.status === 'OPEN') {
    const sections = storeSections(store);
    const fixedMenu = store.fixedMenu ?? null;
    // Com as duas seções, o aviso do cardápio do dia vai na aba Marmitas; aqui só quando não há nada para pedir.
    if (!store.menu && (!fixedMenu || pageTabs(sections.filter((s) => s === 'A_LA_CARTE'), fixedMenu).length === 0)) {
      return (
        <div className="notice notice-warning" role="status">
          {sections.includes('MARMITA') ? (
            <>
              <strong>O cardápio de hoje ainda não foi publicado.</strong> Volte daqui a pouco.
            </>
          ) : (
            <strong>Nenhum item disponível pela página agora.</strong>
          )}
        </div>
      );
    }
    return null;
  }
  const text = STORE_STATUS_TEXT[store.status];
  const tone = store.status === 'UNAVAILABLE' ? 'notice-info' : 'notice-warning';
  return (
    <div className={`notice ${tone}`} role="status">
      <h2>{text.title}</h2>
      <p>{text.body}</p>
      {store.status === 'CLOSED' && store.hoursText && <p>Horário: {store.hoursText}</p>}
    </div>
  );
}
