#!/usr/bin/env bash
# Preparação de uma VM nova (ADR-0060, infra/README.md §3). Ubuntu 24.04 (Oracle Cloud Ampere A1 ou VPS do plano B).
# Roda UMA vez, como root, a partir de uma cópia da pasta infra/:
#
#   scp -r infra ubuntu@<ip>:/tmp/almoseis-infra
#   ssh ubuntu@<ip> 'sudo DEPLOY_SSH_PUBKEY="ssh-ed25519 AAAA... github-deploy" SWAP_SIZE=4G bash /tmp/almoseis-infra/vm/bootstrap-vm.sh'
#
# Faz: pacotes e atualizações automáticas de segurança, fuso, swap, Docker + compose, rotação de log do Docker,
# usuário `deploy` (só chave SSH, no grupo docker), SSH sem senha, fail2ban, firewall (80/443 + SSH),
# /opt/almoseis, cron do backup e logrotate.
#
# Variáveis:
#   DEPLOY_SSH_PUBKEY   chave pública do GitHub Actions (obrigatória). Mais chaves: DEPLOY_SSH_PUBKEY_EXTRA
#   SWAP_SIZE           4G (padrão; produção) | 2G (homologação)
#   REBOOT_TIME         04:30 (reinício automático, se uma atualização exigir)
#
# Idempotente: rodar de novo só confere e reaplica.
set -euo pipefail

[ "$(id -u)" -eq 0 ] || { echo "rode como root (sudo)"; exit 1; }
: "${DEPLOY_SSH_PUBKEY:?defina DEPLOY_SSH_PUBKEY com a chave pública do deploy}"
SWAP_SIZE="${SWAP_SIZE:-4G}"
REBOOT_TIME="${REBOOT_TIME:-04:30}"
SRC_INFRA="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
export DEBIAN_FRONTEND=noninteractive

step() { echo; echo "==> $*"; }

step "pacotes base"
apt-get update -q
apt-get -y -q -o Dpkg::Options::=--force-confold upgrade
apt-get install -y -q ca-certificates curl gnupg jq unattended-upgrades apt-listchanges fail2ban cron logrotate util-linux

step "fuso horário (cron e logs da VM; os contêineres ficam em UTC)"
timedatectl set-timezone America/Sao_Paulo || ln -sf /usr/share/zoneinfo/America/Sao_Paulo /etc/localtime

step "atualizações automáticas de segurança"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
APT::Periodic::AutocleanInterval "7";
EOF
cat > /etc/apt/apt.conf.d/52almoseis-unattended <<EOF
// Só o repositório de segurança da distribuição (o padrão do Ubuntu). Docker fica de fora: atualizar à mão.
Unattended-Upgrade::Remove-Unused-Dependencies "true";
Unattended-Upgrade::Automatic-Reboot "true";
Unattended-Upgrade::Automatic-Reboot-Time "${REBOOT_TIME}";
Unattended-Upgrade::Package-Blacklist { "docker-ce"; "docker-ce-cli"; "containerd.io"; "docker-compose-plugin"; };
EOF
systemctl enable --now unattended-upgrades

step "swap de ${SWAP_SIZE}"
if ! swapon --show | grep -q /swapfile; then
  fallocate -l "$SWAP_SIZE" /swapfile
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi
cat > /etc/sysctl.d/90-almoseis.conf <<'EOF'
vm.swappiness = 10
vm.overcommit_memory = 0
net.core.somaxconn = 1024
EOF
sysctl --system >/dev/null

step "firewall do sistema (80/443 e SSH)"
# Antes do Docker: o `netfilter-persistent save` não grava as regras que o Docker cria.
# Oracle Cloud: as imagens Ubuntu vêm com iptables (netfilter-persistent) que só liberam a 22. Abrimos 80/443 antes
# do REJECT. A Security List / NSG da VCN também precisa liberar 80 e 443 (infra/README.md).
# VPS comum (plano B): ufw. As portas publicadas pelo Docker não passam pelo ufw; o compose só publica 80/443
# (o PostgreSQL fica no 127.0.0.1).
if [ -f /etc/iptables/rules.v4 ] && command -v netfilter-persistent >/dev/null; then
  for rule in "-p tcp --dport 80" "-p tcp --dport 443" "-p udp --dport 443"; do
    # shellcheck disable=SC2086
    iptables -C INPUT -m state --state NEW $rule -j ACCEPT 2>/dev/null \
      || iptables -I INPUT -m state --state NEW $rule -j ACCEPT
  done
  netfilter-persistent save
else
  apt-get install -y -q ufw
  ufw default deny incoming
  ufw default allow outgoing
  ufw allow OpenSSH
  ufw allow 80/tcp
  ufw allow 443/tcp
  ufw allow 443/udp
  ufw --force enable
fi

step "Docker (repositório oficial)"
if ! command -v docker >/dev/null; then
  install -m 0755 -d /etc/apt/keyrings
  # shellcheck disable=SC1091
  . /etc/os-release
  curl -fsSL "https://download.docker.com/linux/${ID}/gpg" -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${ID} ${VERSION_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update -q
  apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi
mkdir -p /etc/docker
cat > /etc/docker/daemon.json <<'EOF'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "5" },
  "live-restore": true,
  "userland-proxy": false
}
EOF
systemctl enable docker
systemctl restart docker

step "usuário deploy"
if ! id deploy >/dev/null 2>&1; then
  adduser --disabled-password --gecos "AlmoSeis deploy" deploy
fi
# Grupo docker = poder de root na VM. É o que o deploy precisa; a chave fica só no GitHub Environment.
usermod -aG docker deploy
install -d -m 700 -o deploy -g deploy /home/deploy/.ssh
echo "$DEPLOY_SSH_PUBKEY" > /home/deploy/.ssh/authorized_keys
if [ -n "${DEPLOY_SSH_PUBKEY_EXTRA:-}" ]; then echo "$DEPLOY_SSH_PUBKEY_EXTRA" >> /home/deploy/.ssh/authorized_keys; fi
chown deploy:deploy /home/deploy/.ssh/authorized_keys
chmod 600 /home/deploy/.ssh/authorized_keys

step "SSH só por chave (porta 22; no Ubuntu 24.04 a porta vem do ssh.socket)"
cat > /etc/ssh/sshd_config.d/99-almoseis.conf <<EOF
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
X11Forwarding no
MaxAuthTries 3
EOF
sshd -t
systemctl reload ssh 2>/dev/null || systemctl reload sshd

step "fail2ban (sshd)"
cat > /etc/fail2ban/jail.d/almoseis.conf <<EOF
[sshd]
enabled = true
port = ssh
maxretry = 5
bantime = 1h
EOF
systemctl enable --now fail2ban
systemctl restart fail2ban

step "/opt/almoseis"
install -d -m 750 -o deploy -g deploy /opt/almoseis /opt/almoseis/infra
install -d -m 750 -o deploy -g deploy /var/log/almoseis
# Cópia inicial da infra (o deploy.yml a substitui a cada deploy).
cp -r "$SRC_INFRA/vm" "$SRC_INFRA/db" /opt/almoseis/infra/
chown -R deploy:deploy /opt/almoseis/infra
if [ ! -f /opt/almoseis/.env ]; then
  install -m 600 -o deploy -g deploy "$SRC_INFRA/vm/.env.example" /opt/almoseis/.env
  echo "    /opt/almoseis/.env criado a partir do .env.example: PREENCHA antes do primeiro deploy."
fi
chmod 600 /opt/almoseis/.env

step "cron do backup e logrotate"
install -m 644 "$SRC_INFRA/vm/backup/almoseis-backup.cron" /etc/cron.d/almoseis
install -m 644 "$SRC_INFRA/vm/backup/almoseis.logrotate" /etc/logrotate.d/almoseis
systemctl enable --now cron

echo
echo "Pronto. Próximos passos (infra/README.md §4):"
echo "  1. preencher /opt/almoseis/.env (como deploy: sudo -u deploy nano /opt/almoseis/.env)"
echo "  2. rodar o workflow deploy.yml (ou deploy.sh à mão)"
echo "  3. conferir: sudo -u deploy bash /opt/almoseis/infra/vm/scripts/dc.sh ps"
