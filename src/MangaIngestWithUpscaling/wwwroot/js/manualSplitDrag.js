// Manual Split Drag & Drop functionality
window.manualSplitDrag = {
    // Initialize drag functionality for a split line
    initDrag: function(elementId, dotNetHelper, initialClientY) {
        const element = document.getElementById(elementId);
        if (!element) return;

        let isDragging = false;
        let startY = initialClientY || 0;
        let startTop = element.offsetTop;
        let container = element.closest('.image-container');
        let magnifier = null;
        let containerImage = null;
        
        // Mouse down - start dragging
        element.addEventListener('mousedown', function(e) {
            if (e.button !== 0) return; // Only left mouse button
            
            isDragging = true;
            startY = e.clientY;
            startTop = element.offsetTop;
            
            // Prevent text selection
            e.preventDefault();
            
            // Add dragging class for visual feedback
            element.classList.add('dragging');
            document.body.style.cursor = 'row-resize';
            
            // Find the image within the container (kept for potential future use)
            containerImage = container.querySelector('img');
            
            // If we have initial mouse position from a previous event, simulate the start of dragging
            if (initialClientY) {
                // Calculate the delta from initial position to current position
                const dy = e.clientY - initialClientY;
                const newTop = startTop + dy;
                
                // Get container dimensions
                const containerRect = container.getBoundingClientRect();
                const minTop = 0;
                const maxTop = containerRect.height - element.offsetHeight;
                
                // Clamp position within container
                const clampedTop = Math.max(minTop, Math.min(newTop, maxTop));
                
                // Update position
                element.style.top = clampedTop + 'px';
                
                // Calculate percentage position (use middle of the line for accuracy)
                const lineHeight = element.offsetHeight;
                const percentage = (clampedTop + lineHeight / 2) / containerRect.height;
                
                // Send update to Blazor
                dotNetHelper.invokeMethodAsync('UpdateDragPosition', percentage);
            }
        });

        // Mouse move - handle dragging
        document.addEventListener('mousemove', function(e) {
            if (!isDragging) return;
            
            // Calculate new position
            const dy = e.clientY - startY;
            const newTop = startTop + dy;
            
            // Get container dimensions
            const containerRect = container.getBoundingClientRect();
            const minTop = 0;
            const maxTop = containerRect.height - element.offsetHeight;
            
            // Clamp position within container
            const clampedTop = Math.max(minTop, Math.min(newTop, maxTop));
            
            // Update position
            element.style.top = clampedTop + 'px';
            
            // Calculate percentage position (use middle of the line for accuracy)
            const lineHeight = element.offsetHeight;
            const percentage = (clampedTop + lineHeight / 2) / containerRect.height;
            
            // Send update to Blazor
            dotNetHelper.invokeMethodAsync('UpdateDragPosition', percentage);
            
            // Magnifier functionality removed - keeping basic drag functionality only
        });

        // Mouse up - end dragging
        document.addEventListener('mouseup', function(e) {
            if (!isDragging) return;
            
            isDragging = false;
            element.classList.remove('dragging');
            document.body.style.cursor = '';
            
            // Magnifier functionality removed
            
            // Prevent the subsequent click event from removing the split
            // by stopping event propagation and preventing default
            e.stopPropagation();
            e.preventDefault();
            
            // Also temporarily disable click handler
            element.style.pointerEvents = 'none';
            setTimeout(function() {
                element.style.pointerEvents = '';
            }, 300); // Slightly longer delay for safety
        });
        
        // Keyboard controls for fine adjustments
        document.addEventListener('keydown', function(e) {
            if (!isDragging) return;
            
            // Prevent default behavior for arrow keys
            if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.key)) {
                e.preventDefault();
            }
            
            // Fine adjustment with arrow keys (1px at a time)
            if (e.key === 'ArrowUp') {
                const newTop = Math.max(0, element.offsetTop - 1);
                element.style.top = newTop + 'px';
                updatePositionFromElement();
            } else if (e.key === 'ArrowDown') {
                const containerRect = container.getBoundingClientRect();
                const maxTop = containerRect.height - element.offsetHeight;
                const newTop = Math.min(maxTop, element.offsetTop + 1);
                element.style.top = newTop + 'px';
                updatePositionFromElement();
            }
            
            // Shift + arrow for faster movement (10px at a time)
            if (e.shiftKey) {
                if (e.key === 'ArrowUp') {
                    const newTop = Math.max(0, element.offsetTop - 10);
                    element.style.top = newTop + 'px';
                    updatePositionFromElement();
                } else if (e.key === 'ArrowDown') {
                    const containerRect = container.getBoundingClientRect();
                    const maxTop = containerRect.height - element.offsetHeight;
                    const newTop = Math.min(maxTop, element.offsetTop + 10);
                    element.style.top = newTop + 'px';
                    updatePositionFromElement();
                }
            }
        });
        
        // Helper function to update position from element
        function updatePositionFromElement() {
            const containerRect = container.getBoundingClientRect();
            const lineHeight = element.offsetHeight;
            const percentage = (element.offsetTop + lineHeight / 2) / containerRect.height;
            dotNetHelper.invokeMethodAsync('UpdateDragPosition', percentage);
        }
        
        // Also prevent context menu
        element.addEventListener('contextmenu', function(e) {
            e.preventDefault();
        });
    },

    // Initialize drag for a specific split line with initial mouse position
    startDragWithPosition: function(elementId, dotNetHelper, clientY) {
        const element = document.getElementById(elementId);
        if (!element) return;
        
        // Store the dotNet helper on the element for access in event handlers
        element._dotNetHelper = dotNetHelper;
        
        let isDragging = true;
        let startY = clientY;
        let startTop = element.offsetTop;
        let container = element.closest('.image-container');
        
        // Add dragging class for visual feedback
        element.classList.add('dragging');
        document.body.style.cursor = 'row-resize';
        
        // Mouse move - handle dragging
        const mouseMoveHandler = function(e) {
            if (!isDragging) return;
            
            // Calculate new position
            const dy = e.clientY - startY;
            const newTop = startTop + dy;
            
            // Get container dimensions
            const containerRect = container.getBoundingClientRect();
            const minTop = 0;
            const maxTop = containerRect.height - element.offsetHeight;
            
            // Clamp position within container
            const clampedTop = Math.max(minTop, Math.min(newTop, maxTop));
            
            // Update position
            element.style.top = clampedTop + 'px';
            
            // Calculate percentage position (use middle of the line for accuracy)
            const lineHeight = element.offsetHeight;
            const percentage = (clampedTop + lineHeight / 2) / containerRect.height;
            
            // Send update to Blazor
            element._dotNetHelper.invokeMethodAsync('UpdateDragPosition', percentage);
        };
        
        // Mouse up - end dragging
        const mouseUpHandler = function(e) {
            if (!isDragging) return;
            
            isDragging = false;
            element.classList.remove('dragging');
            document.body.style.cursor = '';
            
            // Clean up event listeners
            document.removeEventListener('mousemove', mouseMoveHandler);
            document.removeEventListener('mouseup', mouseUpHandler);
            
            // Prevent the subsequent click event from removing the split
            e.stopPropagation();
            e.preventDefault();
            
            // Temporarily disable click handler
            element.style.pointerEvents = 'none';
            setTimeout(function() {
                element.style.pointerEvents = '';
            }, 300);
        };
        
        // Keyboard controls for fine adjustments
        const keyDownHandler = function(e) {
            if (!isDragging) return;
            
            // Prevent default behavior for arrow keys
            if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.key)) {
                e.preventDefault();
            }
            
            // Fine adjustment with arrow keys (1px at a time)
            if (e.key === 'ArrowUp') {
                const newTop = Math.max(0, element.offsetTop - 1);
                element.style.top = newTop + 'px';
                updatePositionFromElement();
            } else if (e.key === 'ArrowDown') {
                const containerRect = container.getBoundingClientRect();
                const maxTop = containerRect.height - element.offsetHeight;
                const newTop = Math.min(maxTop, element.offsetTop + 1);
                element.style.top = newTop + 'px';
                updatePositionFromElement();
            }
            
            // Shift + arrow for faster movement (10px at a time)
            if (e.shiftKey) {
                if (e.key === 'ArrowUp') {
                    const newTop = Math.max(0, element.offsetTop - 10);
                    element.style.top = newTop + 'px';
                    updatePositionFromElement();
                } else if (e.key === 'ArrowDown') {
                    const containerRect = container.getBoundingClientRect();
                    const maxTop = containerRect.height - element.offsetHeight;
                    const newTop = Math.min(maxTop, element.offsetTop + 10);
                    element.style.top = newTop + 'px';
                    updatePositionFromElement();
                }
            }
        };
        
        // Helper function to update position from element
        function updatePositionFromElement() {
            const containerRect = container.getBoundingClientRect();
            const lineHeight = element.offsetHeight;
            const percentage = (element.offsetTop + lineHeight / 2) / containerRect.height;
            element._dotNetHelper.invokeMethodAsync('UpdateDragPosition', percentage);
        }
        
        // Add event listeners
        document.addEventListener('mousemove', mouseMoveHandler);
        document.addEventListener('mouseup', mouseUpHandler);
        document.addEventListener('keydown', keyDownHandler);
        
        // Prevent context menu
        element.addEventListener('contextmenu', function(e) {
            e.preventDefault();
        });
    },

    // Clean up event listeners
    cleanup: function(elementId) {
        const element = document.getElementById(elementId);
        if (element) {
            // Remove all event listeners (simplified cleanup)
            element.classList.remove('dragging');
        }
    }
};