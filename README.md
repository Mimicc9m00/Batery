# Batery

Batery é um aplicativo para Windows que mostra a carga de periféricos conectados. Ele fica na bandeja do sistema e avisa quando a bateria está baixa.

- Detecta dispositivos Bluetooth e Bluetooth LE conectados, controles XInput e dispositivos USB HID compatíveis.
- Consulta a carga em receptores Logitech compatíveis e em modelos da linha SteelSeries Arctis Nova 7.
- Atualiza as leituras automaticamente e oferece a opção de iniciar com o Windows.

O nível de detalhe depende do dispositivo. O Batery mostra a porcentagem quando disponível. Alguns modelos informam apenas uma faixa de carga ou aparecem com a leitura indisponível.

## Compilar e executar

No Windows 10 ou 11, abra o PowerShell na pasta do projeto. O script usa o compilador C# do .NET Framework 4.x e os metadados WinRT do próprio Windows.

```powershell
.\build.ps1
.\dist\Batery.exe
```

Clique no ícone da bandeja para abrir a janela. No menu do ícone você pode atualizar as leituras, configurar a inicialização automática e sair do aplicativo.
