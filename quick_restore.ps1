# Быстрый скрипт для восстановления проекта из бэкапа
param(
    [Parameter(Mandatory=$true)]
    [string]$BackupArchive,
    [switch]$Force = $false
)

Write-Host "Восстановление проекта из бэкапа: $BackupArchive" -ForegroundColor Green

# Проверяем существование архива
if (!(Test-Path $BackupArchive)) {
    Write-Host "Ошибка: Файл бэкапа не найден: $BackupArchive" -ForegroundColor Red
    exit 1
}

try {
    # 1. Создаем временную директорию для распаковки
    $TempDir = ".\temp_restore_$(Get-Date -Format 'HHmmss')"
    Write-Host "Распаковка архива..." -ForegroundColor Yellow
    Expand-Archive -Path $BackupArchive -DestinationPath $TempDir -Force

    # Находим директорию с бэкапом (должна быть одна)
    $BackupDirs = Get-ChildItem $TempDir -Directory
    if ($BackupDirs.Count -ne 1) {
        Write-Host "Ошибка: Неожиданная структура архива" -ForegroundColor Red
        exit 1
    }
    
    $BackupDir = $BackupDirs[0].FullName

    # 2. Читаем информацию о восстановлении
    $RestoreInfoPath = "$BackupDir\restore_info.json"
    if (Test-Path $RestoreInfoPath) {
        $RestoreInfo = Get-Content $RestoreInfoPath | ConvertFrom-Json
        Write-Host "Информация о бэкапе:" -ForegroundColor Cyan
        Write-Host "  Дата создания: $($RestoreInfo.timestamp)"
        Write-Host "  Название: $($RestoreInfo.backup_name)"
        Write-Host ""
    }

    # 3. Проверяем конфликты с существующими файлами
    $AppFiles = @("articles.json", "synonyms.json", "docker-compose.yml", "docker-compose.override.yml")
    $ExistingFiles = $AppFiles | Where-Object { Test-Path $_ }
    
    if ($ExistingFiles.Count -gt 0 -and -not $Force) {
        Write-Host "Предупреждение: Следующие файлы будут перезаписаны:" -ForegroundColor Yellow
        $ExistingFiles | ForEach-Object { Write-Host "  - $_" }
        Write-Host ""
        $Confirm = Read-Host "Продолжить? (y/N)"
        if ($Confirm -ne 'y' -and $Confirm -ne 'Y') {
            Write-Host "Восстановление отменено" -ForegroundColor Yellow
            Remove-Item -Path $TempDir -Recurse -Force
            exit 0
        }
    }

    # 4. Останавливаем существующие контейнеры
    Write-Host "Остановка существующих контейнеров..." -ForegroundColor Yellow
    docker-compose down 2>$null

    # 5. Восстанавливаем файлы приложения
    Write-Host "Восстановление файлов приложения..." -ForegroundColor Yellow
    foreach ($file in $AppFiles) {
        $SourcePath = "$BackupDir\$file"
        if (Test-Path $SourcePath) {
            Copy-Item -Path $SourcePath -Destination "." -Force
            Write-Host "Восстановлен: $file"
        }
    }

    # 6. Восстанавливаем данные Elasticsearch (если есть прямая копия)
    $ElasticsearchDataBackup = "$BackupDir\elasticsearch_data"
    if (Test-Path $ElasticsearchDataBackup) {
        Write-Host "Восстановление данных Elasticsearch (прямая копия)..." -ForegroundColor Yellow
        
        # Удаляем существующие данные
        if (Test-Path ".\elasticsearch_data") {
            Remove-Item -Path ".\elasticsearch_data" -Recurse -Force
        }
        
        # Копируем данные из бэкапа
        Copy-Item -Path $ElasticsearchDataBackup -Destination ".\elasticsearch_data" -Recurse -Force
        Write-Host "Данные Elasticsearch восстановлены"
        
        # Запускаем все сервисы
        Write-Host "Запуск сервисов..." -ForegroundColor Yellow
        docker-compose up -d
        
    } else {
        # 7. Восстанавливаем через API (если нет прямой копии)
        $ApiBackupDir = "$BackupDir\elasticsearch_api"
        if (Test-Path $ApiBackupDir) {
            Write-Host "Восстановление данных Elasticsearch через API..." -ForegroundColor Yellow
            
            # Запускаем только Elasticsearch
            docker-compose up -d elasticsearch
            
            # Ждем готовности Elasticsearch
            Write-Host "Ожидание готовности Elasticsearch (60 секунд)..."
            Start-Sleep -Seconds 60
            
            # Восстанавливаем данные
            & .\scripts\restore_elasticsearch.ps1 -BackupPath $ApiBackupDir -Force
            
            # Запускаем остальные сервисы
            Write-Host "Запуск остальных сервисов..." -ForegroundColor Yellow
            docker-compose up -d
        } else {
            Write-Host "Предупреждение: Данные Elasticsearch не найдены в бэкапе" -ForegroundColor Yellow
            Write-Host "Запуск сервисов без данных..." -ForegroundColor Yellow
            docker-compose up -d
        }
    }

    # 8. Проверяем восстановление
    Write-Host "Проверка восстановления..." -ForegroundColor Yellow
    Start-Sleep -Seconds 10
    
    # Проверяем статус Elasticsearch
    try {
        $HealthResponse = Invoke-RestMethod -Uri "http://localhost:9200/_cluster/health" -Headers @{Authorization = "Basic $([Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('elastic:muVmg+YxSgExd2NKBttV')))"} -TimeoutSec 30
        Write-Host "Статус Elasticsearch: $($HealthResponse.status)" -ForegroundColor Green
        
        # Проверяем количество документов
        $CountResponse = Invoke-RestMethod -Uri "http://localhost:9200/_cat/count/articles?format=json" -Headers @{Authorization = "Basic $([Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('elastic:muVmg+YxSgExd2NKBttV')))"} -TimeoutSec 30
        if ($CountResponse) {
            Write-Host "Документов в индексе: $($CountResponse[0].count)" -ForegroundColor Green
        }
    } catch {
        Write-Host "Предупреждение: Не удалось проверить статус Elasticsearch" -ForegroundColor Yellow
        Write-Host "Проверьте вручную: docker-compose logs elasticsearch" -ForegroundColor Yellow
    }

    # 9. Очищаем временные файлы
    Remove-Item -Path $TempDir -Recurse -Force

    Write-Host "`nВосстановление завершено успешно!" -ForegroundColor Green
    Write-Host "`nДля проверки работы поиска выполните:" -ForegroundColor Cyan
    Write-Host "cd Searcher" -ForegroundColor White
    Write-Host "dotnet run -- search 'тест'" -ForegroundColor White
    
} catch {
    Write-Host "Ошибка при восстановлении: $($_.Exception.Message)" -ForegroundColor Red
    
    # Очищаем временные файлы в случае ошибки
    if (Test-Path $TempDir) {
        Remove-Item -Path $TempDir -Recurse -Force
    }
    
    exit 1
}

