# Быстрый скрипт для создания полного бэкапа проекта
param(
    [string]$BackupName = "backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
)

Write-Host "Создание полного бэкапа проекта: $BackupName" -ForegroundColor Green

# Создаем директорию для бэкапа
$BackupDir = ".\backups\$BackupName"
New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null

try {
    # 1. Останавливаем приложения (но оставляем Elasticsearch)
    Write-Host "Остановка приложений..." -ForegroundColor Yellow
    docker-compose stop searcher scraper 2>$null

    # 2. Создаем бэкап через API
    Write-Host "Создание бэкапа Elasticsearch через API..." -ForegroundColor Yellow
    $ApiBackupDir = "$BackupDir\elasticsearch_api"
    & .\scripts\backup_elasticsearch.ps1 -BackupPath $ApiBackupDir

    # 3. Копируем данные напрямую (если доступны)
    Write-Host "Копирование данных Elasticsearch..." -ForegroundColor Yellow
    if (Test-Path ".\elasticsearch_data") {
        $DataBackupDir = "$BackupDir\elasticsearch_data"
        Copy-Item -Path ".\elasticsearch_data" -Destination $DataBackupDir -Recurse -Force
        Write-Host "Данные Elasticsearch скопированы"
    }

    # 4. Сохраняем файлы приложения
    Write-Host "Сохранение файлов приложения..." -ForegroundColor Yellow
    $AppFiles = @(
        "articles.json",
        "synonyms.json", 
        "docker-compose.yml",
        "docker-compose.override.yml"
    )
    
    foreach ($file in $AppFiles) {
        if (Test-Path $file) {
            Copy-Item -Path $file -Destination $BackupDir -Force
            Write-Host "Скопирован: $file"
        }
    }

    # 5. Создаем архив
    Write-Host "Создание архива..." -ForegroundColor Yellow
    $ArchivePath = ".\backups\$BackupName.zip"
    Compress-Archive -Path $BackupDir -DestinationPath $ArchivePath -Force
    
    # 6. Создаем метаданные
    $Metadata = @{
        timestamp = Get-Date -Format "yyyy-MM-ddTHH:mm:ssZ"
        backup_name = $BackupName
        files_included = $AppFiles | Where-Object { Test-Path $_ }
        elasticsearch_data_included = Test-Path ".\elasticsearch_data"
        archive_path = $ArchivePath
        restore_instructions = @(
            "1. Распакуйте архив: Expand-Archive -Path '$ArchivePath' -DestinationPath '.\restore'",
            "2. Скопируйте файлы в корень проекта",
            "3. Запустите: docker-compose up -d elasticsearch",
            "4. Восстановите данные: .\scripts\restore_elasticsearch.ps1 -BackupPath '.\restore\$BackupName\elasticsearch_api'",
            "5. Запустите приложения: docker-compose up -d"
        )
    }
    
    $Metadata | ConvertTo-Json -Depth 3 | Out-File -FilePath "$BackupDir\restore_info.json" -Encoding UTF8

    # 7. Перезапускаем приложения
    Write-Host "Перезапуск приложений..." -ForegroundColor Yellow
    docker-compose up -d 2>$null

    Write-Host "`nБэкап создан успешно!" -ForegroundColor Green
    Write-Host "Архив: $ArchivePath" -ForegroundColor Cyan
    Write-Host "Размер: $([math]::Round((Get-Item $ArchivePath).Length / 1MB, 2)) MB" -ForegroundColor Cyan
    
    Write-Host "`nДля восстановления на другом устройстве:" -ForegroundColor Yellow
    Write-Host "1. Скопируйте файл: $ArchivePath"
    Write-Host "2. Распакуйте и следуйте инструкциям в restore_info.json"
    
    # Очищаем временную директорию
    Remove-Item -Path $BackupDir -Recurse -Force

} catch {
    Write-Host "Ошибка при создании бэкапа: $($_.Exception.Message)" -ForegroundColor Red
    
    # Перезапускаем приложения в случае ошибки
    docker-compose up -d 2>$null
    exit 1
}
