## Ne değişti? / What changed?

<!-- Kısaca açıklayın; ilgili Issue varsa bağlayın (ör. Fixes #12). / Describe briefly and link the related issue (e.g. Fixes #12). -->

## Kontrol listesi / Checklist

- [ ] Derleme 0 uyarı, 0 hata / Build has 0 warnings and 0 errors (`dotnet build src\RtxWindowsUpdater\RtxWindowsUpdater.csproj -c Release`)
- [ ] Ekranda görünen her yeni metin iki dilde / Every new user-visible string is in both languages (`L.T` / `{l:T}`)
- [ ] Renkler tema anahtarlarından, sabit renk yok / Colors come from theme keys, no hard-coded colors
- [ ] Yeni düğme ve seçimler klavye odağını gösteriyor, UI Automation adı var / New buttons and choices show keyboard focus and have a UI Automation name
- [ ] Sonuçlar gerçek; başarısız işlem başarılı gösterilmiyor / Results are real; failures are never shown as success
- [ ] Sistemi değiştiren bir işlem kullanıcı onayı olmadan çalışmıyor / Nothing changes the system without user approval
- [ ] Şifre, anahtar veya kişisel bilgi yok / No passwords, keys or personal data
- [ ] Kullanıcıya görünen değişiklik için sürüm notu aşağıda / Release note for user-visible changes is below

## Sürüm notu / Release note

<!-- Türkçe ve İngilizce birer cümle. Sürüm numarasını ve CHANGELOG bölümünü proje sahibi ekler. / One sentence each in Turkish and English. The maintainer adds the version number and the CHANGELOG section. -->
