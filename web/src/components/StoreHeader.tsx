import type { PublicStore } from '@/lib/api/types';
import { formatE164, whatsappLink } from '@/lib/phone';
import { formatAddress, STORE_STATUS_TEXT } from '@/lib/texts';

export function StoreHeader({ store }: { store: PublicStore }) {
  const address = formatAddress(store.address);
  const phone = formatE164(store.contactPhone);
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
          {[store.pickupAvailable && 'Retirada no local', store.deliveryAvailable ? 'Entrega' : null]
            .filter(Boolean)
            .join(' · ')}
          {!store.deliveryAvailable && store.pickupAvailable && <span className="muted"> · Entrega em breve</span>}
        </p>
      )}
    </header>
  );
}

export function StoreStatusNotice({ store }: { store: PublicStore }) {
  if (store.status === 'OPEN') {
    if (!store.menu) {
      return (
        <div className="notice notice-warning" role="status">
          <strong>O cardápio de hoje ainda não foi publicado.</strong> Volte daqui a pouco.
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
