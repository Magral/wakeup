# Постоянная подпись Wake Mini

Для Android подготовлен отдельный постоянный RSA-ключ Wake Mini (alias: wakemini). Приватный JKS и пароли находятся в приватной резервной копии владельца, не в репозитории. Открытый SHA-256 сертификата записан в android-signing-certificate.sha256. Release workflow проверяет подпись готового APK перед публикацией.

## Один раз через браузер

1. Откройте GitHub → Magral/wakeup → Settings → Secrets and variables → Actions → New repository secret.
2. Из файла GitHub-Secrets.txt в приватном архиве владельца добавьте ANDROID_KEYSTORE_BASE64, ANDROID_KEYSTORE_PASSWORD, ANDROID_KEY_ALIAS, ANDROID_KEY_PASSWORD. Значения вставляйте целиком, без заголовков и пробелов.
3. Actions → Publish Wake Mini release → Run workflow → main. После успешной проверки подписанный APK появится в текущем релизе.

GitHub CLI и Java на ПК для такого способа не нужны. Не генерируйте другой ключ вместо подготовленного: будущие обновления должны подписываться тем же сертификатом. Старый скрипт Setup-WakeMini-Signing.ps1 предназначался для первоначального создания ключа; при использовании готового архива запускать его не нужно.

Ключ подписи не связан с файлом wake.key Windows-помощника. wake.key менять не требуется. Старые Debug-сборки могли иметь другую подпись, поэтому для первого перехода Android может потребовать переустановку. Настройки подключения можно восстановить из wake.key на ПК или по QR-коду.
