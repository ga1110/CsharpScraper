# PowerShell скрипт для восстановления Elasticsearch индекса из бэкапа
param(
    [string]$BackupPath = ".\elasticsearch_backup",
    [string]$ElasticsearchUrl = "http://localhost:9200",
    [string]$Username = "elastic",
    [string]$Password = "muVmg+YxSgExd2NKBttV",
    [switch]$Force = $false
)

Write-Host "Восстановление Elasticsearch индекса из бэкапа..." -ForegroundColor Green

# Проверяем существование директории бэкапа
if (!(Test-Path $BackupPath)) {
    Write-Host "Директория бэкапа не найдена: $BackupPath" -ForegroundColor Red
    exit 1
}

# Кодируем credentials для Basic Auth
$base64AuthInfo = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes(("${Username}:${Password}")))
$headers = @{
    Authorization = "Basic $base64AuthInfo"
    "Content-Type" = "application/json"
}

try {
    # Проверяем подключение к Elasticsearch
    Write-Host "Проверка подключения к Elasticsearch..."
    $healthResponse = Invoke-RestMethod -Uri "$ElasticsearchUrl/_cluster/health" -Headers $headers -Method Get
    Write-Host "Статус кластера: $($healthResponse.status)" -ForegroundColor Yellow

    # Читаем метаданные бэкапа
    $metadataPath = "$BackupPath/backup_metadata.json"
    if (Test-Path $metadataPath) {
        $metadata = Get-Content $metadataPath | ConvertFrom-Json
        Write-Host "Информация о бэкапе:" -ForegroundColor Cyan
        Write-Host "  Дата создания: $($metadata.timestamp)"
        Write-Host "  Версия Elasticsearch: $($metadata.elasticsearch_version)"
        Write-Host "  Количество индексов: $($metadata.indices_count)"
        Write-Host "  Общее количество документов: $($metadata.total_documents)"
        Write-Host ""
    }

    # Получаем список файлов бэкапа
    $backupFiles = Get-ChildItem "$BackupPath/*_data.json"
    
    if ($backupFiles.Count -eq 0) {
        Write-Host "Файлы данных не найдены в директории бэкапа" -ForegroundColor Red
        exit 1
    }

    foreach ($dataFile in $backupFiles) {
        $indexName = $dataFile.BaseName -replace "_data$", ""
        
        Write-Host "Восстановление индекса: $indexName" -ForegroundColor Cyan
        
        # Проверяем, существует ли индекс
        try {
            $existingIndex = Invoke-RestMethod -Uri "$ElasticsearchUrl/$indexName" -Headers $headers -Method Get -ErrorAction Stop
            
            if (-not $Force) {
                Write-Host "Индекс '$indexName' уже существует. Используйте -Force для перезаписи." -ForegroundColor Yellow
                continue
            } else {
                Write-Host "Удаление существующего индекса '$indexName'..."
                Invoke-RestMethod -Uri "$ElasticsearchUrl/$indexName" -Headers $headers -Method Delete | Out-Null
            }
        } catch {
            # Индекс не существует, это нормально
        }
        
        # Восстанавливаем настройки и маппинги
        $settingsFile = "$BackupPath/${indexName}_settings.json"
        $mappingFile = "$BackupPath/${indexName}_mapping.json"
        
        $createIndexBody = @{
            settings = @{}
            mappings = @{}
        }
        
        if (Test-Path $settingsFile) {
            $settings = Get-Content $settingsFile | ConvertFrom-Json
            # Извлекаем только пользовательские настройки
            if ($settings.$indexName.settings.index.PSObject.Properties) {
                $userSettings = @{}
                foreach ($prop in $settings.$indexName.settings.index.PSObject.Properties) {
                    if ($prop.Name -notin @("creation_date", "uuid", "version", "provided_name")) {
                        $userSettings[$prop.Name] = $prop.Value
                    }
                }
                $createIndexBody.settings = $userSettings
            }
        }
        
        if (Test-Path $mappingFile) {
            $mappings = Get-Content $mappingFile | ConvertFrom-Json
            if ($mappings.$indexName.mappings) {
                $createIndexBody.mappings = $mappings.$indexName.mappings
            }
        }
        
        # Создаем индекс
        $createIndexJson = $createIndexBody | ConvertTo-Json -Depth 10
        Invoke-RestMethod -Uri "$ElasticsearchUrl/$indexName" -Headers $headers -Method Put -Body $createIndexJson | Out-Null
        Write-Host "Индекс '$indexName' создан"
        
        # Восстанавливаем данные
        Write-Host "Загрузка данных в индекс '$indexName'..."
        $documents = Get-Content $dataFile.FullName | ConvertFrom-Json
        
        if ($documents.Count -gt 0) {
            # Подготавливаем bulk запрос
            $bulkBody = ""
            foreach ($doc in $documents) {
                $indexAction = @{
                    index = @{
                        _index = $indexName
                        _id = $doc._id
                    }
                } | ConvertTo-Json -Compress
                
                $docSource = $doc._source | ConvertTo-Json -Compress -Depth 10
                
                $bulkBody += "$indexAction`n$docSource`n"
            }
            
            # Отправляем bulk запрос
            $bulkResponse = Invoke-RestMethod -Uri "$ElasticsearchUrl/_bulk" -Headers $headers -Method Post -Body $bulkBody
            
            $errors = $bulkResponse.items | Where-Object { $_.index.error }
            if ($errors.Count -gt 0) {
                Write-Host "Предупреждение: $($errors.Count) документов не удалось загрузить" -ForegroundColor Yellow
            }
            
            Write-Host "Загружено документов: $($documents.Count - $errors.Count)" -ForegroundColor Green
        }
        
        Write-Host "Индекс '$indexName' восстановлен`n"
    }
    
    # Обновляем индексы
    Write-Host "Обновление индексов..."
    Invoke-RestMethod -Uri "$ElasticsearchUrl/_refresh" -Headers $headers -Method Post | Out-Null
    
    Write-Host "Восстановление завершено успешно!" -ForegroundColor Green
    
} catch {
    Write-Host "Ошибка при восстановлении: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $responseBody = $reader.ReadToEnd()
        Write-Host "Детали ошибки: $responseBody" -ForegroundColor Red
    }
    exit 1
}





