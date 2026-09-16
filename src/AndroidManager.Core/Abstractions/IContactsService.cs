using AndroidManager.Core.Models;

namespace AndroidManager.Core.Abstractions;

public interface IContactsService
{
    Task<IReadOnlyList<PhoneContact>> GetContactsAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PhoneContact>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    Task<PhoneContact> CreateAsync(
        string displayName,
        string phoneNumber,
        string? email = null,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        PhoneContact contact,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        long contactId,
        CancellationToken cancellationToken = default);

    Task<string> ExportToVcfAsync(
        string savePath,
        CancellationToken cancellationToken = default);

    Task<string> ExportToJsonAsync(
        string savePath,
        CancellationToken cancellationToken = default);

    Task<string> ExportToCsvAsync(
        string savePath,
        CancellationToken cancellationToken = default);

    Task<string> ExportToExcelAsync(
        string savePath,
        CancellationToken cancellationToken = default);

    /// <summary>VCF dosyasından kişileri cihaz rehberine yazar. Dönen sayı: başarıyla eklenen.</summary>
    Task<int> ImportFromVcfAsync(
        string filePath,
        CancellationToken cancellationToken = default);

    /// <summary>Normalize edilmiş numara → görünen ad indeksi (SMS eşleştirmesi).</summary>
    Task<IReadOnlyDictionary<string, string>> GetPhoneDisplayNameIndexAsync(
        CancellationToken cancellationToken = default);
}
