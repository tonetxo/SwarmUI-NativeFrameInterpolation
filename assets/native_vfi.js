// Add Native VFI Interpolate button to video context menus via SwarmUI's registerMediaButton API.
(function() {
    if (typeof registerMediaButton !== 'function') {
        console.warn('Native VFI: registerMediaButton is not available - SwarmUI version may be too old');
        return;
    }

    registerMediaButton(
        'VFI Interpolate',
        (src) => {
            if (typeof currentBackendFeatureSet === 'undefined' || !currentBackendFeatureSet.includes('native_frame_interp')) {
                let message = 'Native Frame Interpolation is not available on the current backend.';
                console.warn('Native VFI: ' + message);
                if (typeof showToast === 'function') {
                    showToast(message);
                }
                return;
            }

            let isDataImage = src.startsWith('data:');
            let isDataVideo = isDataImage && src.toLowerCase().startsWith('data:video/');
            let isVideo = isDataVideo || (typeof getMediaType === 'function' ? getMediaType(src) === 'video' : false);
            if (!isDataImage && !isVideo) {
                let extension = src.split('.').pop().toLowerCase().split('?')[0];
                isVideo = ['mp4', 'webm', 'gif', 'mov', 'avi', 'mkv'].includes(extension);
            }

            // Data URL videos are not supported.
            if (isDataVideo || !isVideo) {
                return;
            }

            let input_overrides = { 'images': 1 };

            let fullsrc = typeof getImageFullSrc === 'function' ? getImageFullSrc(src) : src;
            let prefix = typeof getImageOutPrefix === 'function' ? getImageOutPrefix() : 'Output';
            input_overrides['nativevfivideofile'] = prefix + '/' + fullsrc;

            // Read Native VFI params from UI (or use defaults)
            let vfiParams = ['nativevfimodel', 'nativevfimultiplier', 'nativevfiadjustfps'];
            for (let paramId of vfiParams) {
                let elem = document.getElementById('input_' + paramId);
                if (elem) {
                    let toggleElem = document.getElementById('input_' + paramId + '_toggle');
                    if (toggleElem && !toggleElem.checked) {
                        continue;
                    }
                    let val = typeof getInputVal === 'function' ? getInputVal(elem, true) : elem.value;
                    if (val != null && val !== '') {
                        input_overrides[paramId] = val;
                    }
                }
            }

            if (typeof mainGenHandler !== 'undefined' && mainGenHandler.doGenerate) {
                mainGenHandler.doGenerate(input_overrides, {});
            }
        },
        'Interpolate frames of this video using ComfyUI native VFI (RIFE 4.26, etc.)',
        ['video'],  // mediaTypes: 'video' only
        true,       // isDefault: true promotes button to be visible directly on the toolbar
        true        // showInHistory
    );
})();
