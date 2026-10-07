using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace AstraKingdoms.Client.UI
{
    /// <summary>Raw pointer stream over the board image (each touch has its own pointer ID).</summary>
    public sealed class BoardInput : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public event Action<int, Vector2> Down;
        public event Action<int, Vector2> Moved;
        public event Action<int, Vector2> Up;

        public void OnPointerDown(PointerEventData eventData) => Down?.Invoke(eventData.pointerId, eventData.position);

        public void OnDrag(PointerEventData eventData) => Moved?.Invoke(eventData.pointerId, eventData.position);

        public void OnPointerUp(PointerEventData eventData) => Up?.Invoke(eventData.pointerId, eventData.position);
    }
}
